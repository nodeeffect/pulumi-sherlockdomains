open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.SherlockDomains

[<EntryPoint>]
let main args =
    Provider.Serve(args, "0.0.1", (fun _host -> SherlockDomainsProvider()), CancellationToken.None).Wait()
    0
