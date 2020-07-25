module N26DirectImport.Database

open System
open System.Collections.Generic
open FSharp.Data
open LiteDB
open Milekic.YoLo

open Definitions
open DataDefinitions

let nameOfValueField = "Value"
let ynabCollectionName = "Ynab"
let n26CollectionName = "N26"
let settingsCollectionName = "Settings"
let pathOfIsOrphanField = "IsOrphan"
let pathOfBindingField = "Binding"
let pathOfBindingFieldId = "Binding.$id"
let pathOfRawField = "Raw"
let nameOfAccountIdField = "account_id"
let pathOfIdField = "_id"
let pathOfN26DeleteField = "Deleted"

let budgetIdTag = 0
let accountIdTag = 1
let knowledgeTag = 2
let lastUpdateDateTag = 3

let mapper = BsonMapper.Global

mapper.RegisterType(
    (fun x -> match x with
              | None | Some "" -> BsonValue.Null
              | Some x -> BsonValue x),
    fun x -> if x.IsNull then None else Some (x.AsString))

mapper.RegisterType(
    (fun x -> match x with
              | None -> BsonValue.Null
              | Some (x : Guid) -> BsonValue x),
    fun x -> if x.IsNull then None else Some (x.AsGuid))

mapper.RegisterType(
    (fun x -> match x with
              | None -> BsonValue.Null
              | Some (x : decimal) -> BsonValue x),
    fun x -> if x.IsNull then None else Some (x.AsDecimal))

mapper.RegisterType(
    (fun x -> match x with
              | Cleared -> BsonValue "cleared"
              | Reconciled -> BsonValue "reconciled"
              | _ -> BsonValue "uncleared"),
    fun x -> match x.AsString with | "cleared" -> Cleared
                                   | "reconciled" -> Reconciled
                                   | _ -> Uncleared)

mapper.RegisterType(
    (fun (x : (string * string) list) -> failwith "Not supported"),
    fun x ->
        x.AsDocument
        |> Seq.map (fun x -> x.Key, x.Value.AsString)
        |> Seq.toList)

mapper.RegisterType(
    (fun (x : YnabQueryTransaction option) ->
        match x with
        | None -> BsonValue.Null
        | Some x -> mapper.ToDocument x :> BsonValue),
    fun x -> if x.IsNull then None else Some (mapper.ToObject x.AsDocument))

mapper
    .Entity<N26QueryTransaction>()
    .Id((fun x -> x.Id))
    .DbRef(fun x -> x.Binding)
    .Field((fun x -> x.RawFields), "Raw")
|> ignore

mapper
    .Entity<N26RawTransaction>()
    .Field((fun x -> x.MerchantName), "merchantName")
    .Field((fun x -> x.ReferenceText), "referenceText")
    .Field((fun x -> x.VisibleTs), "visibleTS")
    .Field((fun x -> x.Type), "type")
    .Field((fun x -> x.Amount), "amount")
    .Field((fun x -> x.OriginalAmount), "originalAmount")
    .Field((fun x -> x.OriginalCurrency), "originalCurrency")
    .Field((fun x -> x.PartnerName), "partnerName")
    .Field((fun x -> x.Cash26Status), "cash26Status")
|> ignore

mapper
    .Entity<YnabQueryTransaction>()
    .Id((fun x -> x.Id))
|> ignore

mapper
    .Entity<YnabRawTransaction>()
    .Field((fun x -> x.PayeeName), "payee_name")
    .Field((fun x -> x.PayeeId), "payee_id")
    .Field((fun x -> x.Date), "date")
    .Field((fun x -> x.CategoryId), "category_id")
    .Field((fun x -> x.Memo), "memo")
    .Field((fun x -> x.ImportId), "import_id")
    .Field((fun x -> x.Cleared), "cleared")
    .Field((fun x -> x.Amount), "amount")
    .Field((fun x -> x.AccountId), "account_id")
    .Field((fun x -> x.Deleted), "deleted")
|> ignore

type LiteCollection<'T> with
    member collection.TryFindById(id: BsonValue) =
        let x = collection.FindById(id)
        if isNull (box x) then None else Some x

let openDatabase (connectionString : string) =
    let db = new LiteDatabase(connectionString)

    let ynab = db.GetCollection<YnabQueryTransaction> ynabCollectionName
    ynab.EnsureIndex(fun x -> x.IsOrphan) |> ignore
    ynab.EnsureIndex(fun x -> x.Raw.AccountId) |> ignore
    ynab.EnsureIndex(fun x -> x.Raw.Deleted) |> ignore

    let n26 = db.GetCollection<N26QueryTransaction> n26CollectionName
    n26.EnsureIndex(fun x -> x.Binding) |> ignore
    n26.EnsureIndex(fun x -> x.Deleted) |> ignore
    n26.EnsureIndex(fun x -> x.Raw.VisibleTs) |> ignore

    db

let rec jsonToBson = function
    | JsonValue.String x ->
        let isGuid, g = Guid.TryParse x
        if isGuid then BsonValue(g) else

        let isDate, d = DateTime.TryParse(x)
        if isDate
        then BsonValue(DateTime(d.Year, d.Month, d.Day, 0, 0, 0))
        else BsonValue(x)
    | JsonValue.Array x -> BsonValue(Array.map jsonToBson x |> ResizeArray)
    | JsonValue.Boolean x -> BsonValue(x)
    | JsonValue.Float x -> BsonValue(x)
    | JsonValue.Null -> BsonValue()
    | JsonValue.Number x -> BsonValue(x)
    | JsonValue.Record x ->
        x
        |> Seq.map (fun (key, value) -> key, jsonToBson value)
        |> Map.ofSeq
        |> Dictionary<string, BsonValue>
        |> BsonDocument
        :> BsonValue

let fieldsToDocument x =
    x
    |> Seq.map (fun (id, value) -> id, jsonToBson value)
    |> Map.ofSeq
    |> Dictionary<string, BsonValue>
    |> BsonDocument

let copyLeftToRight (left : BsonDocument) (right : BsonDocument) =
    right |> Seq.iter (fun x -> left.Item x.Key <- x.Value)
    left

let makeModels (db : LiteDatabase) =
    let mapper = db.Mapper
    let settings = db.GetCollection(settingsCollectionName)
    let ynabQuery = db.GetCollection<YnabQueryTransaction>(ynabCollectionName)
    let ynabRaw = db.GetCollection(ynabCollectionName)
    let n26Query =
        db.GetCollection<N26QueryTransaction>(n26CollectionName).IncludeAll()
    let n26Raw = db.GetCollection(n26CollectionName)
    let getSetting (tag : int) =
        settings.TryFindById(BsonValue tag)
        |> Option.bind (fun doc -> doc.TryFind nameOfValueField)
    let getGuidSetting tag = getSetting tag |> Option.map (fun x -> x.AsGuid)

    let budgetId = (getGuidSetting budgetIdTag).Value
    let accountId = (getGuidSetting accountIdTag).Value

    let queryModel = {
        GetBudgetId = fun () -> budgetId
        GetAccountId = fun () -> accountId
        GetKnowledgeTag = fun () ->
            match getSetting knowledgeTag with
            | None -> YnabApi.NoKnowledge
            | Some x -> YnabApi.Knowledge x.AsInt32
        GetLastUpdateDate = fun () ->
            let setting = getSetting lastUpdateDateTag
            setting |> Option.map (fun x -> x.AsDateTime)
        GetN26Transactions = fun (from, until) ->
            let toTs =
                DateTimeOffset >> fun x -> x.ToUnixTimeMilliseconds() |> decimal
            let from = toTs from
            let until = toTs until
            n26Query.Find (fun x -> x.Raw.VisibleTs >= from &&
                                    x.Raw.VisibleTs <= until &&
                                    x.Deleted <> true)
        GetN26Transaction = fun guid -> n26Query.TryFindById (BsonValue guid)
        FindUnbound = fun () ->
            n26Query.Find (fun x -> x.Binding = None && x.Deleted <> true)
        FindOrphans = fun () ->
            ynabQuery.Find (fun x -> x.Raw.AccountId = Some accountId &&
                                     x.IsOrphan <> false &&
                                     x.Raw.Deleted <> true)
    }

    let commandModel = {
        UpsertYnab =
            ([], [])
            |> flip Seq.fold <| fun (toUpsert, toBind) (id, raw, ct, binding) ->
                let current = ynabRaw.TryFindById (BsonValue id)
                let toBind =
                    match binding with
                    | None -> toBind
                    | Some None -> Choice1Of2 id::toBind
                    | Some (Some n26Id) -> Choice2Of2 (n26Id, id)::toBind
                let toUpsert =
                    let rawFields = raw |> Option.map fieldsToDocument
                    let includeOrphanField =
                        rawFields
                        |> Option.bind (fun fs -> fs.TryFind nameOfAccountIdField)
                         = Some (BsonValue accountId)

                    let template =
                        current
                        |> flip Option.defaultWith <| fun () ->
                            [
                                yield pathOfIdField, BsonValue id
                                if includeOrphanField then
                                    yield pathOfIsOrphanField, BsonValue true
                            ]
                            |> Map.ofSeq
                            |> Dictionary<string, BsonValue>
                            |> BsonDocument
                    [
                        match raw with
                        | Some fields ->
                            let raw = fieldsToDocument fields
                            let result = BsonDocument()
                            result.Item pathOfRawField <- raw
                            yield result
                        | _ -> ()

                        match ct with
                        | Some command -> yield mapper.ToDocument command
                        | None -> ()

                        match binding with
                        | Some None ->
                            let result = BsonDocument()
                            result.Item pathOfIsOrphanField <- BsonValue true
                            yield result
                        | Some (Some _) ->
                            let result = BsonDocument()
                            result.Item pathOfIsOrphanField <- BsonValue false
                            yield result
                        | None -> ()
                    ]
                    |> List.fold copyLeftToRight template
                    |> fun x -> x::toUpsert
                toUpsert, toBind
            >> fun (toUpsert, toBind) ->
                ynabRaw.Upsert toUpsert |> ignore

                toBind
                |> flip Seq.collect <| function
                    | Choice1Of2 ytBindingToRemove ->
                        Query.EQ(pathOfBindingFieldId, BsonValue ytBindingToRemove)
                        |> n26Raw.Find
                        |> flip Seq.map <| fun d ->
                            d.Item pathOfBindingField <- BsonValue.Null
                            d
                    | Choice2Of2 (n26Id, ytBinding) ->
                        let d = n26Raw.FindById (BsonValue n26Id)
                        let binding = BsonDocument()
                        binding.Item "$id" <- BsonValue (ytBinding)
                        binding.Item "$ref" <- BsonValue ynabCollectionName
                        d.Item pathOfBindingField <- binding
                        Seq.singleton d
                |> n26Raw.Upsert
                |> ignore

        UpsertN26 =
            Seq.map <| fun (id, fields) ->
                let withRaw =
                    let result = BsonDocument()
                    result.Item pathOfIdField <- BsonValue id
                    result.Item pathOfRawField <- fieldsToDocument fields
                    result

                let current = n26Raw.TryFindById (BsonValue id)
                match current with
                | Some x -> copyLeftToRight x withRaw
                | None -> withRaw
            >> n26Raw.Upsert
            >> ignore

        DeleteN26 = fun ids ->
            let n26Transactions =
                ids
                |> flip Seq.map <| fun id ->
                    n26Raw.TryFindById (BsonValue id)
                    |> flip Option.map <| fun c ->
                        c.Item pathOfN26DeleteField <- BsonValue true
                        c
                |> Seq.onlySome
                |> Seq.toList

            n26Transactions
            |> n26Raw.Upsert
            |> ignore

            n26Transactions
            |> flip Seq.map <| fun nt ->
                match nt.TryFind pathOfBindingField with
                | None -> None
                | Some x when x = BsonValue.Null -> None
                | Some x -> ynabRaw.TryFindById (x.AsDocument.Item "$id")
            |> Seq.onlySome
            |> flip Seq.map <| fun doc ->
                doc.Item pathOfIsOrphanField <- BsonValue true
                doc
            |> ynabRaw.Upsert
            |> ignore

        SaveKnowledgeTag = fun x ->
            [
                yield pathOfIdField, BsonValue knowledgeTag
                match x with
                | YnabApi.Knowledge x -> yield nameOfValueField, BsonValue x
                | YnabApi.NoKnowledge -> ()
            ]
            |> Map.ofSeq
            |> Dictionary<string, BsonValue>
            |> BsonDocument
            |> settings.Upsert
            |> ignore

        SaveLastUpdateDate = fun x ->
            let result = BsonDocument()
            result.Item pathOfIdField <- BsonValue lastUpdateDateTag
            result.Item nameOfValueField <- BsonValue x
            settings.Upsert result |> ignore
    }

    queryModel, commandModel
