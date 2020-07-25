module N26DirectImportCore.Definitions

open System

type Cleared = Uncleared | Cleared | Reconciled

[<CLIMutable>]
type YnabRawTransaction = {
    Date : DateTime
    PayeeId : Guid option
    PayeeName : string option
    CategoryId : Guid option
    Memo : string option
    Amount : decimal option
    ImportId : string option
    Cleared : Cleared
    AccountId : Guid option
    Deleted : bool
}
    with
    static member Template = {
        Date = DateTime.Today
        PayeeName = None
        PayeeId = None
        CategoryId = None
        Memo = None
        Amount = None
        ImportId = None
        Cleared = Uncleared
        AccountId = None
        Deleted = false
    }

[<CLIMutable>]
type YnabQueryTransaction = {
    Id : string
    OriginalAmount : decimal option
    OriginalCurrency : string option
    Raw : YnabRawTransaction
    IsOrphan : bool
}
    with
    member t.Amount = t.Raw.Amount |> Option.map (fun x -> x / 1000.0m)

type YnabCommandTransaction = {
    OriginalAmount : decimal option
    OriginalCurrency : string option
}

[<CLIMutable>]
type N26RawTransaction = {
    MerchantName : string option
    ReferenceText : string option
    VisibleTs : decimal
    Type : string
    Amount : decimal
    OriginalAmount : decimal option
    OriginalCurrency : string option
    PartnerName : string option
    Cash26Status : string option
}

[<CLIMutable>]
type N26QueryTransaction = {
    Id : Guid
    Binding : YnabQueryTransaction option
    Raw : N26RawTransaction
    RawFields : (string * string) list
    Deleted : bool
}
    with
    member t.Date =
        int64 t.Raw.VisibleTs
        |> DateTimeOffset.FromUnixTimeMilliseconds
        |> fun x -> DateTime(x.Year, x.Month, x.Day, 0, 0, 0, DateTimeKind.Utc)
