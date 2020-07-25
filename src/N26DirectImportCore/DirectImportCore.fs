module N26DirectImportCore.DirectImportCore

open System
open FSharp.Data
open Milekic.YoLo
open Definitions

let fetchYnab
    getTransactionsFromApi
    upsertTransactions
    saveKnowledgeTag
    budgetId knowledgeTag =

    let newKnowledgeTag, changes = getTransactionsFromApi budgetId knowledgeTag

    let upsertArguments =
        changes
        |> flip Seq.map <| fun (yt : YnabApi.TransactionsResponse.Transaction) ->
            yt.Id.String.Value,
            Some (yt.JsonValue.Properties() |> Seq.ofArray),
            None,
            (if yt.Deleted = Some true then Some None else None)

    upsertTransactions upsertArguments
    saveKnowledgeTag newKnowledgeTag
    ()

let fetchN26
    getTransactionsFromApi getTransactionsFromDatabase
    upsertTransactions deleteTransactions
    saveLastUpdateDate
    range =

    let transactionsFromApi =
        let range = DateTimeOffset(fst range), DateTimeOffset(snd range)
        getTransactionsFromApi range |> List.ofSeq

    let transactionsFromDatabase : N26QueryTransaction list =
        getTransactionsFromDatabase range |> List.ofSeq

    let deleteArguments =
        let idsFromApi =
            transactionsFromApi
            |> flip Seq.map <| fun (nt : N26Api.TransactionsResponse.Transaction) ->
                nt.Id
            |> Set
        transactionsFromDatabase
        |> Seq.where (fun t -> Set.contains (t.Id) idsFromApi |> not)
        |> Seq.map (fun t -> t.Id)

    let upsertArguments =
        transactionsFromApi
        |> Seq.map (fun t -> t.Id, t.JsonValue.Properties() |> Array.toSeq)

    upsertTransactions upsertArguments
    deleteTransactions deleteArguments
    saveLastUpdateDate (snd range)
    ()

let updateFolder (toAdd, toUpdate, orphans) nt =
    let commandTransaction : YnabCommandTransaction = {
        OriginalAmount = nt.Raw.OriginalAmount
        OriginalCurrency = nt.Raw.OriginalCurrency
    }

    let doNothing = toAdd, toUpdate, orphans

    let add () =
        let updated =
            Rules.applyAddRules nt.Raw nt.RawFields YnabRawTransaction.Template

        (updated, commandTransaction, nt.Id)::toAdd,
        toUpdate,
        orphans

    let update (qt : YnabQueryTransaction) =
        let updated = Rules.applyUpdateRules nt.Raw nt.RawFields qt.Raw
        if updated = qt.Raw
        then doNothing
        else
            toAdd,
            (qt.Id, Some (qt.Raw, updated), None)::toUpdate,
            orphans

    let bindAndUpdate (qt : YnabQueryTransaction) =
        let updated = Rules.applyUpdateRules nt.Raw nt.RawFields qt.Raw
        let rawUpdate = if updated = qt.Raw then None else Some (qt.Raw, updated)

        toAdd,
        (qt.Id, rawUpdate, Some (nt.Id, commandTransaction))::toUpdate,
        Set.remove qt orphans

    let bind (qt : YnabQueryTransaction) =
        toAdd,
        (qt.Id, None, Some (nt.Id, commandTransaction))::toUpdate,
        Set.remove qt orphans

    match nt.Binding  with
    | Some (yt : YnabQueryTransaction) when yt.Raw.Cleared <> Reconciled ->
        update yt
    | Some _ -> doNothing
    | None ->
        let relevantOrphans =
            let originalAmountMatches =
                orphans
                |> flip Seq.where <| fun o ->
                    o.OriginalAmount = nt.Raw.OriginalAmount &&
                    o.OriginalCurrency = nt.Raw.OriginalCurrency
            let amountMatches =
                orphans |> Seq.where (fun o -> o.Amount = Some nt.Raw.Amount)

            if nt.Raw.OriginalAmount.IsSome &&
                not <| Seq.isEmpty originalAmountMatches
            then originalAmountMatches
            else amountMatches

        if Seq.isEmpty relevantOrphans
        then add ()
        else

        let timeDifference (qt : YnabQueryTransaction) = qt.Raw.Date - nt.Date
        let orphan = Seq.minBy timeDifference relevantOrphans

        if orphan.Raw.Cleared = Reconciled
        then bind orphan
        else bindAndUpdate orphan

let private fieldMappings =
    let mapString x = x |> Option.map (fun x -> x.ToString())

    [
        "date", fun (t : Definitions.YnabRawTransaction) ->
            t.Date.ToString("yyyy-MM-dd") |> Some
        "payee_id", fun t -> t.PayeeId |> mapString
        "payee_name", fun t -> t.PayeeName
        "category_id", fun t -> t.CategoryId |> mapString
        "memo", fun t -> t.Memo
        "import_id", fun t -> t.ImportId
        "cleared", fun t -> match t.Cleared with
                            | Cleared -> Some "cleared"
                            | Reconciled -> Some "reconciled"
                            | Uncleared -> Some "uncleared"
        "amount", fun t -> t.Amount |> mapString
    ]

let private fieldsToTransaction fields =
    if fields |> Seq.isEmpty then None else

    fields
    |> Seq.map (fun (k, v) -> k, JsonValue.String v)
    |> Array.ofSeq
    |> JsonValue.Record
    |> YnabApi.TransactionsResponse.Transaction
    |> Some

let getUpdateTransaction id original updated =
    fieldMappings
    |> Seq.map (fun (k, vf) -> k, vf original, vf updated)
    |> Seq.where (fun (k, original, updated) -> original <> updated)
    |> Seq.map (fun (k, _, v) ->
        let vs = match v with | Some x -> x | None -> "" in k, vs)
    |> List.ofSeq
    |> function
        | [] -> None
        | tail -> ("id", id)::tail |> fieldsToTransaction

let getCreateTransaction accountId t =
    fieldMappings
    |> Seq.map (fun (k, vf) -> vf t |> Option.map (fun v -> k, v))
    |> Seq.onlySome
    |> List.ofSeq
    |> fun tail -> ("account_id", accountId.ToString())::tail
    |> fieldsToTransaction
    |> Option.get

let updateYnabTransactions ynabHeaders budgetId transactions =
    transactions
    |> Seq.map (fun (id, o, u) -> getUpdateTransaction id o u)
    |> Seq.onlySome
    |> fun t ->
        if Seq.isEmpty t then Ok (Array.empty) else
        YnabApi.updateYnabTransactions ynabHeaders budgetId t

let addYnabTransactions ynabHeaders budgetId accountId transactions =
    if Seq.isEmpty transactions then Ok (Array.empty) else

    transactions
    |> Seq.map (getCreateTransaction accountId)
    |> YnabApi.addYnabTransactions ynabHeaders budgetId

let getChangeSet orphans =
    Seq.sortBy (fun (x : N26QueryTransaction) -> x.Date)
    >> Seq.fold updateFolder ([], [], orphans)

let applyChangeSet
    addYnabTransactions updateYnabTransactions
    upsertYnab
    toAdd
    toUpdate = result {

    let toAddSorted =
        toAdd
        |> flip Seq.sortBy <| fun (x : YnabRawTransaction, _, _) ->
            x.Amount, x.Date, x.Memo

    let! added =
        toAddSorted
        |> Seq.map (fun (raw, _, _) -> raw)
        |> addYnabTransactions
        |> flip Result.map <|
            Seq.sortBy (fun (x : YnabApi.TransactionsResponse.Transaction) ->
                x.Amount, x.Date, x.Memo)
        |> flip Result.map <| fun added ->
            toAddSorted
            |> Seq.zip added
            |> flip Seq.map <|
                fun (t : YnabApi.TransactionsResponse.Transaction,
                     (_, ct, n26Id)) ->
                (t.Id.String.Value),
                Some (t.JsonValue.Properties() |> Seq.ofArray),
                Some ct,
                Some (Some n26Id)

    let! updatedTransactions =
        toUpdate
        |> flip Seq.map <| fun (id, update, _) ->
            update |> Option.map (fun (o, u) -> id, o, u)
        |> Seq.onlySome
        |> updateYnabTransactions
        |> flip Result.map <|
            (Seq.map (fun (x : YnabApi.TransactionsResponse.Transaction) ->
                x.Id.String.Value, x)
            >> Map.ofSeq)

    let updated =
        toUpdate
        |> flip Seq.map <| fun (id, _, bind) ->
            let properties =
                updatedTransactions
                |> Map.tryFind id
                |> Option.map (fun t -> t.JsonValue.Properties() |> Seq.ofArray)
            let n26Id, ct =
                bind
                |> Option.map (fun (n26Id, ct) -> Some (Some n26Id), Some ct)
                |> Option.defaultValue (None, None)
            id, properties, ct, n26Id

    added |> Seq.append updated |> upsertYnab
}
