open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.SherlockDomains

[<EntryPoint>]
let main args =
    Provider.Serve(args, SherlockDomainsProvider.Version, (fun _host -> new SherlockDomainsProvider()), CancellationToken.None).Wait()
    0
