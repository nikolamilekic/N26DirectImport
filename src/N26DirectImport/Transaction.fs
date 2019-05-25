namespace N26DirectImport

open System

type Cleared = Uncleared | Cleared | Reconciled

type TransactionModel =
    {
        Id : string
        Date : DateTime
        PayeeId : Guid option
        PayeeName : string option
        CategoryId : Guid option
        Memo : string option
        Amount : decimal option
        ImportId : string option
        Cleared : Cleared
    }

module TransactionModel =
    let makeEmpty date = {
        Id = ""
        Date = date
        PayeeName = None
        PayeeId = None
        CategoryId = None
        Memo = None
        Amount = None
        ImportId = None
        Cleared = Uncleared
    }
