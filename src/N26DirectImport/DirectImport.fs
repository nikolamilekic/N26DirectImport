module N26DirectImport.DirectImport

open System
open DataDefinitions
open DirectImportCore

let fetchAll queryModel commandModel ynabHeaders n26HeadersRetriever range = async {
    let getYnabTransactionsFromApi = YnabApi.getTransactions ynabHeaders
    let budgetId = queryModel.GetBudgetId ()
    let knowledgeTag = queryModel.GetKnowledgeTag ()

    let! fetchYnab = Async.StartChild <| async {
        return fetchYnab
            getYnabTransactionsFromApi
            commandModel.UpsertYnab
            commandModel.SaveKnowledgeTag
            budgetId knowledgeTag
    }

    let! n26Headers = n26HeadersRetriever
    let getN26TransactionsFromApi = N26Api.getTransactions n26Headers

    fetchN26
        getN26TransactionsFromApi queryModel.GetN26Transactions
        commandModel.UpsertN26 commandModel.DeleteN26
        commandModel.SaveLastUpdateDate
        range

    do! fetchYnab
}

let applyChangeSet queryModel commandModel ynabHeaders =
    let budgetId = queryModel.GetBudgetId ()
    let accountId = queryModel.GetAccountId ()

    let addTransactions = addYnabTransactions ynabHeaders budgetId accountId
    let updateTransactions = updateYnabTransactions ynabHeaders budgetId

    applyChangeSet addTransactions updateTransactions (commandModel.UpsertYnab)

let sync
    queryModel
    commandModel
    ynabHeaders
    n26Credentials = async {

    let! n26HeadersRetriever =
        Async.StartChild (N26Api.makeHeaders n26Credentials)
    let! accountInfoRetriever =
        async {
            let! n26Headers = n26HeadersRetriever
            return! N26Api.getAccountInfo n26Headers
        }
        |> Async.StartChild

    let orphans = queryModel.FindOrphans () |> Set
    let unbound = queryModel.FindUnbound ()
    let lastUpdateDate = queryModel.GetLastUpdateDate ()
    let start =
        seq {
            yield! orphans |> Seq.map (fun x -> x.Raw.Date)
            match lastUpdateDate with
            | Some x -> yield x
            | _ -> ()
            yield DateTime.UtcNow.Date
        }
        |> Seq.min
    let range = start, DateTime.Now

    do! fetchAll queryModel commandModel ynabHeaders n26HeadersRetriever range

    let toAdd, toUpdate, _ = getChangeSet orphans unbound
    let changeSet = applyChangeSet queryModel commandModel ynabHeaders toAdd toUpdate

    let! accountInfo = accountInfoRetriever

    return changeSet, accountInfo
}
