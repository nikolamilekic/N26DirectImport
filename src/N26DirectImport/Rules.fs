module N26DirectImport.Rules

open System
open FSharp.Data
open Milekic.YoLo

let private typesToClear = [ "PT"; "DT"; "CT"; "DD"; "AV"; "PF" ]

let private makeImportId () = Guid.NewGuid().ToString()

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
    yield fun yt (nt : N26Transactions.Transaction) ->
        {
            yt with
                ImportId = if yt.ImportId.IsSome
                           then yt.ImportId else makeImportId () |> Some
                Cleared = if List.contains nt.Type typesToClear
                          then Cleared else yt.Cleared
                Date = getDateFromN26Transaction nt
                Amount = nt.Amount |> Some
        }
    yield!
        [
            List.singleton "Rundfunk",
                fun yt -> { yt with PayeeName = Some "ARD" }
            List.singleton "micro-v2-atm",
                fun yt -> { yt with PayeeId = Some Payees.wallet }
            List.singleton "DM-Drogerie Markt",
                fun yt -> { yt with PayeeName = Some "DM" }
            List.singleton "Rewe",
                fun yt -> { yt with PayeeName = Some "Supermarket" }
            List.singleton "50021120",
                fun yt -> { yt with CategoryId = Some Categories.savings }
            List.singleton "DE28650910400075316005",
                fun yt -> { yt with PayeeId = Some Payees.volksbank }
            List.singleton "Uber",
                fun yt -> { yt with PayeeName = Some "Uber" }
            List.singleton "www.appcargo.com",
                fun yt -> { yt with PayeeName = Some "Car:Go" }
            List.singleton "JAVNI PREVOZ",
                fun yt -> { yt with PayeeName = Some "BusPlus" }
            [ "Amazon"; "AMZN" ],
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
            List.singleton "MSFT",
                fun yt -> { yt with PayeeName = Some "Microsoft" }
            List.singleton "Adobe",
                fun yt ->
                    { yt with
                        PayeeName = Some "Adobe"
                        CategoryId = Some Categories.subscriptions }
            List.singleton "Nintendo",
                fun yt -> { yt with PayeeName = Some "Nintendo" }
            List.singleton "micro-v2-travel-holidays",
                fun yt ->
                    { yt with
                        CategoryId =
                            yt.CategoryId
                            |> Option.defaultValue Categories.vacation
                            |> Some }
            List.singleton "Center Parcs",
                fun yt -> { yt with PayeeName = Some "Center Parcs"
                                    CategoryId = None }
            List.singleton "EnBW AG",
                fun yt -> { yt with PayeeName = Some "EnBW" }
            List.singleton "bodo",
                fun yt -> { yt with PayeeName = Some "bodo" }
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
        if List.contains t.CurrencyCode [ "RSD"; "RUB" ]
        then
            { yt with
                CategoryId =
                    yt.CategoryId
                    |> Option.defaultValue Categories.vacation
                    |> Some }
        else yt
    yield fun yt t ->
        match yt.Memo, makeMetadata t with
        | x, "" -> x
        | None, metadata -> Some metadata
        | Some x, metadata when x.ToLower().Contains (metadata.ToLower()) ->
            Some x
        | Some x, metadata -> Some (sprintf "%s %s" x metadata)
        |> fun memo -> { yt with Memo = memo }
    yield fun yt _ ->
        match yt.Memo with
        | Some x when x.Length > 100 ->
            { yt with Memo = x.Substring(0, 100) |> Some }
        | _ -> yt
    yield fun yt t ->
        if t.PartnerName <> Some "Cash26" then yt else
        { yt with
            Cleared = if t.Cash26Status = Some "PAID"
                      then Cleared else Uncleared
            PayeeId = Some Payees.wallet }
    yield fun yt t ->
        if yt.PayeeName.IsSome || yt.PayeeId.IsSome then yt else
        { yt with PayeeName = t.MerchantName }
}

let applyAddRules yt nt = Seq.fold (fun yt rule -> rule yt nt) yt rules
let applyUpdateRules yt nt =
    let afterRules = Seq.fold (fun yt rule -> rule yt nt) yt rules
    {
        yt with
            Date = afterRules.Date
            Memo = afterRules.Memo
            Cleared = afterRules.Cleared
            Amount = afterRules.Amount
    }
