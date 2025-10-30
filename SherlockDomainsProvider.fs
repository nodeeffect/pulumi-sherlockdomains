namespace Pulumi.SherlockDomains

open System
open System.Collections.Immutable
open System.Linq
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Text.Json

open Pulumi
open Pulumi.Experimental
open Pulumi.Experimental.Provider

type SherlockDomainsProvider(?apiToken: string) =
    inherit Pulumi.Experimental.Provider.Provider()

    let httpClient = new HttpClient()

    static let dnsRecordResourceName = "sherlockdomains:index:DnsRecord"
    static let nameServersResourceName = "sherlockdomains:index:NameServers"
    static let apiBaseUrl = "https://api.sherlockdomains.com"

    static let apiTokenEnvVarName = "SHERLOCKDOMAINS_API_TOKEN"

    static member val Version = "0.0.7"
    
    member private self.ApiToken 
        with set(token: string) = 
            httpClient.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)

    member private self.GetDnsRecordPropertyString(dict: ImmutableDictionary<string, PropertyValue>, name: string) =
        match dict.[name].TryGetString() with
        | true, value -> value
        | false, _ -> failwith $"No {name} property in {dnsRecordResourceName}"

    member private self.GetDnsRecordPropertyInt(dict: ImmutableDictionary<string, PropertyValue>, name: string) =
        match dict.[name].TryGetNumber() with
        | true, value -> int value
        | false, _ -> failwith $"No {name} property in {dnsRecordResourceName}"
    
    override self.GetSchema (request: GetSchemaRequest, ct: CancellationToken): Task<GetSchemaResponse> = 
        let schema =
            let dnsRecordProperties = 
                """{
                                "domainId": {
                                    "type": "string"
                                },
                                "type": {
                                    "type": "string"
                                },
                                "name": {
                                    "type": "string"
                                },
                                "value": {
                                    "type": "string"
                                },
                                "ttl": {
                                    "type": "integer"
                                }
                            }"""

            let nameServersProperties = 
                """{
                                "domainId": {
                                    "type": "string"
                                },
                                "servers": {
                                    "type": "array",
                                    "items": {
                                        "type": "string"
                                    }
                                }
                            }"""

            sprintf
                """{
                    "name": "sherlockdomains",
                    "version": "%s",
                    "resources": {
                        "%s" : {
                            "properties": %s,
                            "inputProperties": %s
                        },
                        "%s" : {
                            "properties": %s,
                            "inputProperties": %s
                        }
                    },
                    "provider": {
                    }
                }"""
                SherlockDomainsProvider.Version
                dnsRecordResourceName
                dnsRecordProperties
                dnsRecordProperties
                nameServersResourceName
                nameServersProperties
                nameServersProperties

        Task.FromResult <| GetSchemaResponse(Schema = schema)

    override self.CheckConfig (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.DiffConfig (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        Task.FromResult <| DiffResponse()

    override self.Configure (request: ConfigureRequest, ct: CancellationToken): Task<ConfigureResponse> = 
        let apiToken = Environment.GetEnvironmentVariable apiTokenEnvVarName
        if String.IsNullOrWhiteSpace apiToken then
            failwith $"Environment variable {apiTokenEnvVarName} was not found!"
        self.ApiToken <- apiToken.Trim()
        Task.FromResult <| ConfigureResponse()

    member private self.AsyncUpdateOrCreateDnsRecord(properties: ImmutableDictionary<string, PropertyValue>, ?maybeId: string): Async<string> =
        async {
            let domainId = self.GetDnsRecordPropertyString(properties, "domainId")
            let recordType = self.GetDnsRecordPropertyString(properties, "type")
            let name = self.GetDnsRecordPropertyString(properties, "name")
            let value = self.GetDnsRecordPropertyString(properties, "value")
            let ttl = self.GetDnsRecordPropertyInt(properties, "ttl")
            
            let uri = $"{apiBaseUrl}/api/v0/domains/{domainId}/dns/records"
            let data = 
                let record = {| ``type`` = recordType; name = name; value = value; ttl = ttl |}
                let updatedRecord = 
                    match maybeId with
                    | Some id -> box {| record with id = id |}
                    | None -> box record
                {|
                    records = [ updatedRecord ]
                |}

            let! response = httpClient.PostAsync(uri, Json.JsonContent.Create data) |> Async.AwaitTask
            let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                
            if response.StatusCode <> HttpStatusCode.OK then
                return failwith $"SherlockDomains server returned error ({response.StatusCode}). Response: {responseContent}"
            else
                return JsonDocument.Parse(responseContent).RootElement.GetProperty("records").[0].GetProperty("id").GetString()
        }

    member public self.AsyncGetNameservers (domainId: string) =
        async {
            let uri = $"{apiBaseUrl}/api/v0/domains/domains"
            let! response = httpClient.GetAsync uri |> Async.AwaitTask
                
            if response.StatusCode <> HttpStatusCode.OK then
                return failwith $"SherlockDomains server returned error (code {response.StatusCode})"
            else
                let! responseJson = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let records = JsonDocument.Parse(responseJson).RootElement.GetProperty("records")
                match records.EnumerateArray() |> Seq.tryFind(fun record -> record.GetProperty("id").GetString() = domainId) with
                | Some record ->
                    return Seq.toArray <| record.GetProperty("nameservers").EnumerateArray()
                | None -> 
                    return failwith $"Domain with id={domainId} not found"
        }

    member private self.AsyncUpdateNameServers(properties: ImmutableDictionary<string, PropertyValue>): Async<unit> =
        async {
            let domainId = 
                match properties.["domainId"].TryGetString() with
                | true, value -> value
                | false, _ -> failwith $"No domainId property in {nameServersResourceName}"
            let servers = 
                match properties.["servers"].TryGetArray() with
                | true, arr -> 
                    arr 
                    |> Seq.map 
                        (fun value -> 
                            match value.TryGetString() with
                            | true, str -> str
                            | false, _ -> failwith $"Non-string type in servers array in {nameServersResourceName}")
                    |> Seq.toArray
                | false, _ -> failwith $"No servers property in {dnsRecordResourceName}"
            
            let uri = $"{apiBaseUrl}/api/v0/domains/{domainId}/nameservers"
            let data = {| nameservers = servers |}

            let! response = httpClient.PatchAsync(uri, Json.JsonContent.Create data) |> Async.AwaitTask
            let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                
            if response.StatusCode <> HttpStatusCode.OK then
                return failwith $"SherlockDomains server returned error ({response.StatusCode}). Response: {responseContent}"
        }

    override self.Check (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        if request.Type = dnsRecordResourceName then
            let ttl = self.GetDnsRecordPropertyInt(request.NewInputs, "ttl")
            let minTtl, maxTtl = 3600, 2592001
            let failures =
                if ttl < minTtl || ttl > maxTtl then
                    Array.singleton <| CheckFailure("ttl", $"Not in range [{minTtl}, {maxTtl}]")
                else
                    Array.empty
            Task.FromResult <| CheckResponse(Inputs = request.NewInputs, Failures = failures)
        elif request.Type = nameServersResourceName then
            Task.FromResult <| CheckResponse(Inputs = request.NewInputs)
        else
            failwith $"Unknown resource type '{request.Type}'"

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        if request.Type = dnsRecordResourceName || request.Type = nameServersResourceName then
            let diff = request.NewInputs.Except request.OldInputs 
            let replaces = diff |> Seq.map (fun pair -> pair.Key) |> Seq.toArray
            Task.FromResult <| DiffResponse(Changes = (replaces.Length > 0), Replaces = replaces)
        else
            failwith $"Unknown resource type '{request.Type}'"

    member private self.AsyncCreate(request: CreateRequest): Async<CreateResponse> =
        async {
            if request.Type = dnsRecordResourceName then
                let! id = self.AsyncUpdateOrCreateDnsRecord request.Properties
                return CreateResponse(Id = id, Properties = request.Properties)
            elif request.Type = nameServersResourceName then
                do! self.AsyncUpdateNameServers request.Properties
                return CreateResponse(Id = System.Guid.NewGuid().ToString(), Properties = request.Properties)
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Create (request: CreateRequest, ct: CancellationToken): Task<CreateResponse> = 
        Async.StartAsTask(self.AsyncCreate request, TaskCreationOptions.None, ct)

    member private self.AsyncUpdate(request: UpdateRequest): Async<UpdateResponse> =
        async {
            if request.Type = dnsRecordResourceName then
                let properties = request.Olds.AddRange request.News
                do! self.AsyncUpdateOrCreateDnsRecord(properties, request.Id) |> Async.Ignore<string>
                return UpdateResponse(Properties = properties)
            elif request.Type = nameServersResourceName then
                let properties = request.News
                do! self.AsyncUpdateNameServers properties
                return UpdateResponse(Properties = properties)
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        Async.StartAsTask(self.AsyncUpdate request, TaskCreationOptions.None, ct)
    
    member private self.AsyncDelete(request: DeleteRequest): Async<unit> =
        async {
            if request.Type = dnsRecordResourceName then
                let domainId = self.GetDnsRecordPropertyString(request.Properties, "domainId")
                let uri = $"{apiBaseUrl}/api/v0/domains/{domainId}/dns/records/{request.Id}"
                let! response = httpClient.DeleteAsync(uri) |> Async.AwaitTask
                
                if response.StatusCode <> HttpStatusCode.OK then
                    let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return failwith $"SherlockDomains server returned error ({response.StatusCode}). Response: {responseContent}"
                else
                    return ()
            elif request.Type = nameServersResourceName then
                // do nothing
                return ()
            else
                failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Delete (request: DeleteRequest, ct: CancellationToken): Task = 
        Async.StartAsTask(self.AsyncDelete request, TaskCreationOptions.None, ct)

    member private self.AsyncRead (request: ReadRequest) : Async<ReadResponse> =
        async {
            if request.Type = dnsRecordResourceName then
                let domainId = self.GetDnsRecordPropertyString(request.Properties, "domainId")
                let uri = $"{apiBaseUrl}/api/v0/domains/{domainId}/dns/records"
                let! response = httpClient.GetAsync(uri) |> Async.AwaitTask
                
                if response.StatusCode <> HttpStatusCode.OK then
                    return failwith $"SherlockDomains server returned error (code {response.StatusCode})"
                else
                    let! responseJson = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let records = JsonDocument.Parse(responseJson).RootElement.GetProperty("records")
                    match records.EnumerateArray() |> Seq.tryFind(fun record -> record.GetProperty("id").GetString() = request.Id) with
                    | Some record ->
                        let properties = 
                            [ for prop in record.EnumerateObject() do 
                                  if request.Properties.ContainsKey prop.Name then
                                      let value = 
                                          if prop.Value.ValueKind = JsonValueKind.String then
                                              PropertyValue(prop.Value.GetString())
                                          elif prop.Value.ValueKind = JsonValueKind.Number then
                                              PropertyValue(prop.Value.GetInt32())
                                          else
                                              failwith $"Unexpected type: {prop.Value.ValueKind}"
                                      yield prop.Name, value ]
                            |> dict
                        return ReadResponse(Id = request.Id, Properties = properties)
                    | None -> 
                        return failwith $"Record with id={request.Id} not found"
            elif request.Type = nameServersResourceName then
                let domainId = 
                    match request.Properties.["domainId"].TryGetString() with
                    | true, value -> value
                    | false, _ -> failwith $"No domainId property in {nameServersResourceName}"

                let! nameservers = self.AsyncGetNameservers domainId
                let nameserversAsPropertyValues =
                    nameservers
                    |> Array.map (fun prop -> PropertyValue(prop.GetString()))
                let properties =
                    dict
                        [ "domainId", PropertyValue domainId
                          "nameservers", PropertyValue(ImmutableArray.Create<PropertyValue> nameserversAsPropertyValues) ]
                return ReadResponse(Id = request.Id, Properties = properties)
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
