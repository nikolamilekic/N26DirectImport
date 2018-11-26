namespace N26DirectImport.Core

open System

module Categories =
    let savings = Guid("f79c97c2-61fd-427c-98f2-120de9d8a4de")
    let vacation = Guid("4d765b29-17a4-455d-a5a2-ad799422a483")
    let subscriptions = Guid("72445b45-e4cc-45c0-9e32-2b91c0d144ce")

module Payees =
    let invest80 = Guid("084a9a7d-d729-4901-8822-a5eb1626feec")
    let savings = Guid("63e89b2b-c108-42a5-9c99-3f4fa7194a9a")
    let volksbank = Guid("62386eeb-eb67-4b3d-afcc-3490840983b3")

module Accounts =
    let n26 = Guid("01b3d10c-f050-4161-a7d5-4ddd3f3310b4")

module Budgets =
    let main = "1f585f96-3781-4f29-97c0-1f4124e26468"
