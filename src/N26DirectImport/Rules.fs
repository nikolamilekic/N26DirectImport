module N26DirectImport.Rules

open System
open FSharp.Data
open Milekic.YoLo

open Definitions

let private typesToClear = [ "PT"; "DT"; "CT"; "DD"; "AV"; "PF" ]

let private makeImportId () = Guid.NewGuid().ToString()

let private makeMetadata nt =
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

let getDateFromN26Transaction nt =
    nt.VisibleTs
    |> int64
    |> DateTimeOffset.FromUnixTimeMilliseconds
    |> fun d -> d.ToLocalTime().DateTime

module Categories =
    let savings = Guid("f79c97c2-61fd-427c-98f2-120de9d8a4de")
    let vacation = Guid("4d765b29-17a4-455d-a5a2-ad799422a483")
    let subscriptions = Guid("72445b45-e4cc-45c0-9e32-2b91c0d144ce")

module Payees =
    let invest80 = Guid("084a9a7d-d729-4901-8822-a5eb1626feec")
    let savings = Guid("63e89b2b-c108-42a5-9c99-3f4fa7194a9a")
    let volksbank = Guid("62386eeb-eb67-4b3d-afcc-3490840983b3")
    let wallet = Guid("65956d9d-d8d6-4ad8-8e57-1719b9bfff37")
    let amexGreen = Guid("10cb1508-09cd-4b41-b254-4778d8d2c80e")

let private rules = seq {
    yield fun yt nt (_ : (string * _) list) ->
        {
            yt with
                ImportId = if yt.ImportId.IsSome
                           then yt.ImportId else makeImportId () |> Some
                Cleared = if List.contains nt.Type typesToClear
                          then Cleared else yt.Cleared
                Date = (getDateFromN26Transaction nt).Date
                Amount = Math.Round(nt.Amount * 1000.0m) |> Some
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
            [ "EnBW AG"; "8168491459" ] ,
                fun yt -> { yt with PayeeName = Some "EnBW" }
            List.singleton "bodo",
                fun yt -> { yt with PayeeName = Some "bodo" }
            List.singleton "3750-111136-71006",
                fun yt -> { yt with PayeeId = Some Payees.amexGreen }
            List.singleton "Gehalt allgemein",
                fun yt -> { yt with PayeeName = Some "aleon" }
        ]
        |> Seq.map (
            fun (values, fs) yt _ ntFields ->
                let matches =
                    ntFields
                    |> List.exists (fun (_, (p : string)) ->
                        values
                        |> List.exists
                            (fun c ->
                                p.ToLower().Contains(c.ToLower())))

                if not matches then yt else fs yt)
    yield fun yt t _ ->
        match t.OriginalCurrency with
        | Some x when List.contains x [ "RSD"; "RUB" ] ->
            { yt with
                CategoryId =
                    yt.CategoryId
                    |> Option.defaultValue Categories.vacation
                    |> Some }
        | _ -> yt
    yield fun yt t _ ->
        match yt.Memo, makeMetadata t with
        | x, "" -> x
        | None, metadata -> Some metadata
        | Some x, metadata when x.ToLower().Contains (metadata.ToLower()) ->
            Some x
        | Some x, metadata -> Some (sprintf "%s %s" x metadata)
        |> fun memo -> { yt with Memo = memo }
    yield fun yt _ _ ->
        match yt.Memo with
        | Some x when x.Length > 100 ->
            { yt with Memo = x.Substring(0, 100) |> Some }
        | _ -> yt
    yield fun yt t _ ->
        if t.PartnerName <> Some "Cash26" then yt else
        { yt with
            Cleared = if t.Cash26Status = Some "PAID"
                      then Cleared else Uncleared
            PayeeId = Some Payees.wallet }
    yield fun yt t _ ->
        if yt.PayeeName.IsSome || yt.PayeeId.IsSome then yt else
        { yt with PayeeName = t.MerchantName }
}

let applyAddRules nt ntFields yt =
    Seq.fold (fun yt rule -> rule yt nt ntFields) yt rules
let applyUpdateRules nt ntFields yt =
    let afterRules = Seq.fold (fun yt rule -> rule yt nt ntFields) yt rules
    {
        yt with
            Date = afterRules.Date
            Memo = afterRules.Memo
            Cleared = afterRules.Cleared
            Amount = afterRules.Amount
    }
