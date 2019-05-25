#r "paket:
nuget Fake.Core.Target
nuget Fake.DotNet.Cli
nuget Fake.IO.Zip
nuget Fake.DotNet.AssemblyInfoFile
nuget Fake.DotNet.Paket
nuget FSharp.Data
nuget Argu
nuget FSharp.Core //"
#load "./.fake/build.fsx/intellisense.fsx"

open System.IO

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open FSharp.Data
open Argu

type Argument =
    | [<CustomAppSettings("APPVEYOR_BUILD_NUMBER")>] BuildNumber of int
    | [<CustomAppSettings("AzureDeploymentToken")>] AzureDeploymentToken of string
with interface IArgParserTemplate with member __.Usage = " "

let arguments =
    ArgumentParser
        .Create()
        .Parse(
            ignoreUnrecognized = true,
            configurationReader = ConfigurationReader.FromEnvironmentVariables())

let productName = "N26DirectImport"
let solution = productName + ".sln"
let binPath = "src/N26DirectImport/bin/Release/netcoreapp2.1"
let publishPath = binPath </> "publish"
let zipFileName = "publish.zip"
let buildNumber = arguments.GetResult(BuildNumber, defaultValue = 9999)

Target.create "PaketRestore" <| fun _ -> Paket.restore id
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
                        "Basic " + (arguments.GetResult AzureDeploymentToken)
                ],
            httpMethod = HttpMethod.Post,
            body = body
        )

    if response.StatusCode <> 200 then response.ToString() |> failwith
Target.create "UpdateAssemblyInfo" <| fun _ ->
    let (|Fsproj|Csproj|) (projectFileName : string) =
        match projectFileName with
        | f when f.EndsWith("fsproj") -> Fsproj
        | f when f.EndsWith("csproj") -> Csproj
        | _ -> failwith (sprintf "Project file %s not supported. Unknown project type." projectFileName)

    let version = sprintf "1.%i.0.0" buildNumber
    !! "src/**/*.??proj"
    |> Seq.iter (fun projectPath ->
        let projectName = Path.GetFileNameWithoutExtension projectPath
        let directoryName = Path.GetDirectoryName projectPath
        let attributes = [
            AssemblyInfo.Title projectName
            AssemblyInfo.Product productName
            AssemblyInfo.Version version
            AssemblyInfo.FileVersion version
        ]
        match projectPath with
        | Fsproj ->
            AssemblyInfoFile.createFSharp
                (directoryName </> "AssemblyInfo.fs")
                attributes
        | Csproj ->
            AssemblyInfoFile.createCSharp
                (directoryName </> "Properties" </> "AssemblyInfo.cs")
                attributes)

Target.create "appveyor" ignore

"PaketRestore"
==> "Clean"
==> "Build"
==> "CopyBuildOutput"
==> "Publish"
==> "Zip"
==> "Release"
==> "appveyor"

"UpdateAssemblyInfo" ?=> "Build"
"UpdateAssemblyInfo" ==> "appveyor"

Target.runOrDefaultWithArguments "CopyBuildOutput"
