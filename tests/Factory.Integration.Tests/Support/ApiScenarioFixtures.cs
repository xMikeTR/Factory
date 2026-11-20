using Factory.Core;
using Microsoft.FSharp.Core;
using FsCore = global::Factory.Core.Core;

namespace Factory.Integration.Tests.Support;

/// designc's scenarios/2 example (ORD-1-ts-01 on tagOrders) with the requests scenarios/3 will carry, written by hand until C4b.
public static class ApiScenarioFixtures
{
    public const string Source = "contracts/openapi/orders.yaml";
    public const string Yaml = "openapi: 3.0.0\npaths:\n  /orders/tags:\n    post: { operationId: tagOrders, responses: { \"200\": { description: ok } } }\n";

    private static Microsoft.FSharp.Collections.FSharpList<Tuple<string, Microsoft.FSharp.Collections.FSharpList<string>>> Rows(string entity, params string[] rows) =>
        FsCore.ToList([Tuple.Create(entity, FsCore.ToList(rows))]);

    public static ApiScenario Scenario(bool withRequests = true) =>
        new("ORD-1-ts-01", "test-scenarios/ORD-1-ts-01.yaml", "0801fb767b1f662d9a80e2b2ab9e77cb1fe37333f1abcc1b360ea0f3a92e2f29", "tagOrders", "POST", "/orders/tags",
            FsCore.ToList([
                new ApiExample(1, "prefix match",
                    Rows("orders/Order", "{\"id\":1,\"ref\":\"A-100\"}", "{\"id\":2,\"ref\":\"B-200\"}"),
                    Rows("orders/Order", "{\"id\":1,\"tag\":\"Priority\"}", "{\"id\":2,\"tag\":null}"),
                    withRequests ? FSharpOption<ApiRequest>.Some(new ApiRequest(FSharpOption<string>.Some("{\"prefix\":\"A\",\"tag\":\"Priority\"}"), FsCore.ToList(Array.Empty<Tuple<string, string>>()))) : FSharpOption<ApiRequest>.None),
                new ApiExample(2, "whole reference",
                    FsCore.ToList(Array.Empty<Tuple<string, Microsoft.FSharp.Collections.FSharpList<string>>>()),
                    Rows("orders/Order", "{\"id\":2,\"tag\":\"Hold\"}"),
                    withRequests ? FSharpOption<ApiRequest>.Some(new ApiRequest(FSharpOption<string>.Some("{\"ref\":\"B-200\",\"tag\":\"Hold\"}"), FsCore.ToList(Array.Empty<Tuple<string, string>>()))) : FSharpOption<ApiRequest>.None)]));

    public static EntityDef Order() =>
        new("orders/Order", "orders", "Order", 1, FsCore.ToList([Tuple.Create("ref", "string"), Tuple.Create("tag", "string")]));

    public static Handover Shape(Handover b, bool withRequests = true) =>
        b.WithApiScenarios(b.Elements.Head.Id.Value, Source, Yaml, [Scenario(withRequests)], [Order()]);
}
