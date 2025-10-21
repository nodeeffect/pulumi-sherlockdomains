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

type SherlockDomainsProvider() =
    inherit Pulumi.Experimental.Provider.Provider()

    let httpClient = new HttpClient()

    static let dnsRecordResourceName = "sherlockdomains:index:DnsRecord"
    static let apiBaseUrl = "https://api.sherlockdomains.com"

    static let apiTokenEnvVarName = "SHERLOCKDOMAINS_API_TOKEN"

    static member val Version = "0.0.4"
    
    member val private ApiToken = "" with get, set

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

            sprintf
                """{
                    "name": "sherlockdomains",
                    "version": "%s",
                    "resources": {
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

    member private self.UpdateOrCreateDnsRecord(properties: ImmutableDictionary<string, PropertyValue>, ?maybeId: string): Async<string> =
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

            httpClient.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", self.ApiToken)
            let! response = httpClient.PostAsync(uri, Json.JsonContent.Create data) |> Async.AwaitTask
            let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                
            if response.StatusCode <> HttpStatusCode.OK then
                return failwith $"SherlockDomains server returned error ({response.StatusCode}). Response: {responseContent}"
            else
                return JsonDocument.Parse(responseContent).RootElement.GetProperty("records").[0].GetProperty("id").GetString()
        }

    override self.Check (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        if request.Type <> dnsRecordResourceName then
            failwith $"Unknown resource type '{request.Type}'"
        else
            let ttl = self.GetDnsRecordPropertyInt(request.NewInputs, "ttl")
            let minTtl, maxTtl = 3600, 2592001
            let failures =
                if ttl < minTtl || ttl > maxTtl then
                    Array.singleton <| CheckFailure("ttl", $"Not in range [{minTtl}, {maxTtl}]")
                else
                    Array.empty
            Task.FromResult <| CheckResponse(Inputs = request.NewInputs, Failures = failures)

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        if request.Type <> dnsRecordResourceName then
            failwith $"Unknown resource type '{request.Type}'"
        else
            let diff = request.NewInputs.Except request.OldInputs 
            let replaces = diff |> Seq.map (fun pair -> pair.Key) |> Seq.toArray
            Task.FromResult <| DiffResponse(Changes = (replaces.Length > 0), Replaces = replaces)

    override self.Create (request: CreateRequest, ct: CancellationToken): Task<CreateResponse> = 
        if request.Type <> dnsRecordResourceName then
            failwith $"Unknown resource type '{request.Type}'"
        else
            async {
                let! id = self.UpdateOrCreateDnsRecord request.Properties
                return CreateResponse(Id = id, Properties = request.Properties)
            }
            |> Async.StartAsTask

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        if request.Type <> dnsRecordResourceName then
            failwith $"Unknown resource type '{request.Type}'"
        else
            async {
                let properties = request.Olds.AddRange request.News
                do! self.UpdateOrCreateDnsRecord(properties, request.Id) |> Async.Ignore<string>
                return UpdateResponse(Properties = properties)
            }
            |> Async.StartAsTask

    override self.Delete (request: DeleteRequest, ct: CancellationToken): Task = 
        if request.Type <> dnsRecordResourceName then
            failwith $"Unknown resource type '{request.Type}'"
        else
            async {
                let domainId = self.GetDnsRecordPropertyString(request.Properties, "domainId")
                httpClient.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", self.ApiToken)
                let uri = $"{apiBaseUrl}/api/v0/domains/{domainId}/dns/records/{request.Id}"
                let! response = httpClient.DeleteAsync(uri) |> Async.AwaitTask
                
                if response.StatusCode <> HttpStatusCode.OK then
                    let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return failwith $"SherlockDomains server returned error ({response.StatusCode}). Response: {responseContent}"
                else
                    return ()
            }
            |> Async.StartAsTask
            :> Task

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        if request.Type <> dnsRecordResourceName then
            failwith $"Unknown resource type '{request.Type}'"
        else
            async {
                let domainId = self.GetDnsRecordPropertyString(request.Properties, "domainId")
                httpClient.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", self.ApiToken)
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
            }
            |> Async.StartAsTask
