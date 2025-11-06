open System
open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.SherlockDomains

[<EntryPoint>]
let main args =
    let apiToken = 
        let privKey = Environment.GetEnvironmentVariable SherlockDomainsProvider.PrivateKeyEnvVarName
        if not <| String.IsNullOrWhiteSpace privKey then
            SherlockDomainsProvider.Authenticate privKey |> Async.RunSynchronously
        else
            // Allow empty token for cases when provider is used to get schema for SDK.
            // Do a check in SherlockDomainsProvider.Configure method instead.
            Environment.GetEnvironmentVariable SherlockDomainsProvider.ApiTokenEnvVarName
    
    Provider.Serve(args, SherlockDomainsProvider.Version, (fun _host -> new SherlockDomainsProvider(apiToken)), CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    0
