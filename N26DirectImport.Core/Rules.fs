module N26DirectImport.Core.Rules

open System
open FSharp.Data

let private typesToClear = [ "PT"; "DT"; "CT"; "DD" ]

let private makeImportId (nt : N26Transactions.Transaction) =
    [
        "Nikola's N26 script"
        nt.VisibleTs.ToString()
    ]
    |> curry String.Join "-"

let findMatchingYnabTransaction transactions (nt : N26Transactions.Transaction) =
    let amount = nt.Amount
    let importId = makeImportId nt
    transactions
    |> List.tryFind (fun (_, yt) -> yt.ImportId = Some importId)
    |> Option.orElseWith (fun () ->
        transactions
        |> List.tryFind (fun (_, yt) ->
            yt.ImportId = None && yt.Amount = Some amount))

let tryGetDateFromImportId yt =
    match yt.ImportId with
    | Some s when s.StartsWith("Nikola's N26 script-") ->
        s.Substring("Nikola's N26 script-".Length) |> int64 |> Some
    | _ -> None

let private makeMetadata (nt : N26Transactions.Transaction) =
    [
        "Merchant: ", nt.MerchantName
        "Reference: ", nt.ReferenceText
    ]
    |> Seq.map (fun (key, value) ->
        value
        |> Option.bind (fun v ->
            if String.IsNullOrWhiteSpace(v) then None else Some (key, v)))
    |> Seq.onlySome
    |> Seq.map (fun (name, value) -> sprintf "%s%s" name value)
    |> curry String.Join " "

let getDateFromN26Transaction (nt : N26Transactions.Transaction) =
    nt.VisibleTs
    |> DateTimeOffset.FromUnixTimeMilliseconds
    |> fun d -> d.DateTime

let private rules = seq {
    yield fun yt nt ->
        {
            yt with
                ImportId = makeImportId nt |> Some
                Cleared = if List.contains nt.Type typesToClear
                          then Cleared else yt.Cleared
                Date = getDateFromN26Transaction nt
                Amount = nt.Amount |> Some
        }

    yield!
        [
            List.singleton "Rewe",
                fun yt -> { yt with PayeeName = Some "Supermarket" }
            List.singleton "50021120",
                fun yt -> { yt with CategoryId = Some Categories.savings }
            List.singleton "DE28650910400075316005",
                fun yt -> { yt with PayeeId = Some Payees.volksbank }
            List.singleton "Uber",
                fun yt -> { yt with PayeeName = Some "Uber" }
            List.singleton "Amazon",
                fun yt -> { yt with PayeeName = Some "Amazon" }
            List.singleton "APOTHEKE",
                fun yt -> { yt with PayeeName = Some "Pharmacy" }
            List.singleton "DB Vertrieb GmbH",
                fun yt -> { yt with PayeeName = Some "DB" }
            List.singleton "ITUNES.COM/BILL",
                fun yt -> { yt with PayeeName = Some "Apple" }
            [
                "From Main Account to Savings"
                "From Savings to Main Account"
            ], fun yt -> { yt with PayeeId = Some Payees.savings }
            [
                "Kulturbrauerei Ltk"
                "Stadtwirt-Ital. Spezia"
            ], fun yt -> { yt with PayeeName = Some "Restaurant" }
            List.singleton "BACKBLAZE",
                fun yt -> { yt with PayeeName = Some "Backblaze" }
            List.singleton "Adobe",
                fun yt ->
                    { yt with
                        PayeeName = Some "Adobe"
                        CategoryId = Some Categories.subscriptions }
        ]
        |> Seq.map (
            fun (values, fs) yt (nt : N26Transactions.Transaction) ->
                let matches =
                    nt.JsonValue.Properties()
                    |> Array.exists (fun (_, p) ->
                        values
                        |> List.exists
                            (fun c ->
                                p.InnerText().ToLower().Contains(c.ToLower())))

                if not matches then yt else fs yt)

    yield fun yt t ->
        match yt.Memo, makeMetadata t with
        | x, "" -> x
        | None, metadata -> Some metadata
        | Some x, metadata when x.ToLower().Contains (metadata.ToLower()) -> Some x
        | Some x, metadata -> Some (sprintf "%s %s" x metadata)
        |> fun memo -> { yt with Memo = memo }

    yield fun yt _ ->
        match yt.Memo with
        | Some x when x.Length > 100 ->
            { yt with Memo = x.Substring(0, 100) |> Some }
        | _ -> yt
}

let apply yt nt =
    let afterRules = Seq.fold (fun yt rule -> rule yt nt) yt rules
    {
        Date = afterRules.Date
        PayeeName = Option.orElse afterRules.PayeeName yt.PayeeName
        PayeeId = Option.orElse afterRules.PayeeId yt.PayeeId
        CategoryId = Option.orElse afterRules.CategoryId yt.CategoryId
        Memo = afterRules.Memo
        Amount = afterRules.Amount
        ImportId = afterRules.ImportId
        Cleared = afterRules.Cleared
    }
