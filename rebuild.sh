#!/bin/sh -e
echo Rebuilding Ixian S2...
echo Cleaning previous build
dotnet clean --configuration Release
echo Restoring packages
dotnet restore
echo Building Ixian S2
dotnet build --configuration Release
echo Done rebuilding Ixian S2