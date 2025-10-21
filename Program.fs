open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.SherlockDomains

[<EntryPoint>]
let main args =
    Provider.Serve(args, SherlockDomainsProvider.Version, (fun _host -> SherlockDomainsProvider()), CancellationToken.None).Wait()
    0
