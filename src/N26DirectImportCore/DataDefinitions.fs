module N26DirectImportCore.DataDefinitions

open System
open FSharp.Data
open Definitions

[<NoComparison; NoEquality>]
type QueryModel = {
    GetBudgetId : unit -> Guid
    GetAccountId : unit -> Guid
    GetKnowledgeTag : unit -> YnabApi.KnowledgeTag
    GetLastUpdateDate : unit -> DateTime option

    GetN26Transactions : DateTime * DateTime -> N26QueryTransaction seq
    GetN26Transaction : Guid -> N26QueryTransaction option

    FindUnbound : unit -> N26QueryTransaction seq
    FindOrphans : unit -> YnabQueryTransaction seq
}

[<NoComparison; NoEquality>]
type CommandModel = {
    SaveKnowledgeTag : YnabApi.KnowledgeTag -> unit
    SaveLastUpdateDate : DateTime -> unit

    UpsertYnab :
        (string *
        (string * JsonValue) seq option *
        YnabCommandTransaction option *
        (Guid option option))
        seq
        -> unit

    UpsertN26 : (Guid * (string * JsonValue) seq) seq -> unit
    DeleteN26 : Guid seq -> unit
}
