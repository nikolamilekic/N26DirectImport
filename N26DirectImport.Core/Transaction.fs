namespace N26DirectImport.Core

open System

type Cleared = Uncleared | Cleared | Reconciled

type TransactionModel =
    {
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
        Date = date
        PayeeName = None
        PayeeId = None
        CategoryId = None
        Memo = None
        Amount = None
        ImportId = None
        Cleared = Uncleared
    }
