#!/bin/sh -e
echo Rebuilding Ixian S2...
echo Cleaning previous build
msbuild S2Node.sln /p:Configuration=Release /target:Clean
echo Removing packages
rm -rf packages
echo Restoring packages
nuget restore S2Node.sln
echo Building Ixian S2
msbuild S2Node.sln /p:Configuration=Release
echo Done rebuilding Ixian S2