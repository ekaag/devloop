cd C:\code\ekaag\devloop
dotnet new console -n devloop.Orchestrator
dotnet new classlib -n devloop.Core
dotnet new xunit -n devloop.Tests

dotnet sln add devloop.orchestrator
dotnet sln add devloop.core
dotnet sln add devloop.tests

dotnet add devloop.orchestrator reference devloop.core
dotnet add devloop.tests reference devloop.core