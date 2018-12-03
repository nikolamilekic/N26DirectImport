#!/bin/bash

dotnet restore dotnet-fake.csproj
dotnet fake run build.fsx $@
