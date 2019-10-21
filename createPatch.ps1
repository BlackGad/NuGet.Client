If(Test-Path safePluginLogging){
    Write-Host "Removing the safePluginLogging directory"
    Remove-Item -Recurse .\safePluginLogging
}

If(Test-Path safePluginLogging.tar){
    Write-Host "Removing the safePluginLogging.tar"
    Remove-Item -Recurse .\safePluginLogging.tar
}


If(Test-Path safePluginLogging.zip){
    Write-Host "Removing the safePluginLogging.zip"
    Remove-Item -Recurse .\safePluginLogging.zip
}

msbuild /restore /t:build /p:Configuration=Release .\src\NuGet.Core\NuGet.Build.Tasks\NuGet.Build.Tasks.csproj
msbuild /restore /t:build /p:Configuration=Release .\src\NuGet.Core\NuGet.CommandLine.XPlat\     
msbuild /restore /t:build /p:Configuration=Release .\src\NuGet.Core\Microsoft.Build.NuGetSdkResolver\ 

mkdir -p safePluginLogging

Copy-Item artifacts\NuGet.Build.Tasks\16.0\bin\Release\netstandard2.0\NuGet.*.dll safePluginLogging
Copy-Item src\NuGet.Core\NuGet.Build.Tasks\NuGet.targets safePluginLogging
Copy-Item artifacts\NuGet.CommandLine.XPlat\16.0\bin\Release\netcoreapp2.1\NuGet.*.dll safePluginLogging
Copy-Item artifacts\Microsoft.Build.NuGetSdkResolver\16.0\bin\Release\netstandard2.0\Microsoft.Build.NuGetSdkResolver.dll safePluginLogging

7z a -tzip safePluginLogging.zip .\safePluginLogging\*.*
7z a -ttar safePluginLogging.tar .\safePluginLogging\*.*

If(Test-Path safePluginLogging){
    Write-Host "Removing the safePluginLogging directory"
    Remove-Item -Recurse .\safePluginLogging
}