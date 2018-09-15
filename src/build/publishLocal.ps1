param([string]$betaver)

if ([string]::IsNullOrEmpty($betaver)) {
	$version = [Reflection.AssemblyName]::GetAssemblyName((resolve-path '..\interface\ISimpleHttpListener.Rx\bin\Release\netstandard2.0\ISimpleHttpListener.Rx.dll')).Version.ToString(3)
	}
else {
	$version = [Reflection.AssemblyName]::GetAssemblyName((resolve-path '..\interface\ISimpleHttpListener.Rx\bin\Release\netstandard2.0\ISimpleHttpListener.Rx.dll')).Version.ToString(3) + "-" + $betaver
}

.\build.ps1 $version

nuget.exe push -Source "1iveowlNuGetRepo" -ApiKey key ".\NuGet\SimpleHttpListener.Rx.$version.symbols.nupkg"
