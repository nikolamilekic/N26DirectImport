#r "paket:
nuget Fake.Core.Target
nuget Fake.DotNet.Cli
nuget Fake.IO.Zip
nuget FSharp.Data
nuget FSharp.Core //"
#load "./.fake/build.fsx/intellisense.fsx"

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open FSharp.Data

let solution = "N26DirectImport.sln"
let binPath = "src/N26DirectImport.FunctionsApp/bin/Release/netcoreapp2.1"
let publishPath = binPath </> "publish"
let zipFileName = "publish.zip"

Target.create "Clean" <| fun _ ->
    Seq.allPairs [|"src"|] [|"bin"; "obj"|]
    |> Seq.collect (fun (x, y) -> !!(sprintf "%s/**/%s" x y))
    |> Seq.append [|"bin"; "obj"|]
    |> Shell.deleteDirs

    File.delete "publish.zip"
Target.create "Build" <| fun _ -> DotNet.build id solution
Target.create "Publish" <| fun _ -> DotNet.publish id solution
Target.create "CopyBuildOutput" <| fun _ ->
    Shell.copyDir "bin" binPath (fun _ -> true)
Target.create "Zip" <| fun _ ->
    !! (publishPath </> "**")
    |> Zip.zip publishPath zipFileName
Target.create "Release" <| fun _ ->
    let body =
        File.readAsBytes zipFileName
        |> HttpRequestBody.BinaryUpload
    let response =
        Http.Request(
            "https://N26DirectImport.scm.azurewebsites.net/api/zipdeploy",
            headers =
                [
                    "Authorization",
                        "Basic " + (Environment.environVarOrFail "AzureDeploymentToken")
                ],
            httpMethod = HttpMethod.Post,
            body = body
        )

    if response.StatusCode <> 200 then response.ToString() |> failwith

"Clean" ==> "Build" ==> "CopyBuildOutput" ==> "Publish" ==> "Zip" ==> "Release"

Target.runOrDefault "CopyBuildOutput"
