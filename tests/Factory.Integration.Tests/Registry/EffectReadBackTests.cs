using System.Text.Json;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class EffectReadBackTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static FeatureEffect Effect(string method = "GET", string path = "/api/exports/:id", int? status = 200, string? shape = null) =>
        new(method, path, status, shape is null ? null : J(shape));
    private static readonly Dictionary<string, string> Id7 = new() { ["id"] = "7" };

    [Fact]
    public void A_given_status_must_equal_and_an_absent_one_means_any_2xx()
    {
        Assert.Null(EffectReadBack.CheckStatus("GET", "/x", 200, 200));
        Assert.Equal("effect GET /x: status 500, expected 200", EffectReadBack.CheckStatus("GET", "/x", 500, 200));
        Assert.Equal("effect GET /x: status 204, expected 200", EffectReadBack.CheckStatus("GET", "/x", 204, 200));
        Assert.Null(EffectReadBack.CheckStatus("GET", "/x", 204, null));
        Assert.Null(EffectReadBack.CheckStatus("GET", "/x", 299, null));
        Assert.Equal("effect GET /x: status 404, expected 2xx", EffectReadBack.CheckStatus("GET", "/x", 404, null));
        Assert.Equal("effect GET /x: status 301, expected 2xx", EffectReadBack.CheckStatus("GET", "/x", 301, null));
    }

    private static string? Shape(string expected, string actual) => EffectReadBack.CheckShape("GET", "/api/exports/7", J(expected), J(actual));

    [Fact]
    public void An_object_needs_every_expected_key_and_recurses_and_extra_keys_are_fine()
    {
        Assert.Null(Shape("""{"rows":[{"cedant":"text:Aa"}]}""", """{"rows":[{"cedant":"Acme","extra":1}],"more":true}"""));
        Assert.Equal("effect GET /api/exports/7: rows[0].cedant is missing", Shape("""{"rows":[{"cedant":"text:Aa"}]}""", """{"rows":[{"other":"x"}]}"""));
        Assert.Equal("effect GET /api/exports/7: total is missing", Shape("""{"total":"num:0"}""", """{}"""));
        Assert.Equal("effect GET /api/exports/7: body is an array, expected object", Shape("""{"a":"x"}""", """[]"""));
    }

    [Fact]
    public void Leaf_kinds_follow_the_prefix_and_other_strings_mean_a_string()
    {
        Assert.Null(Shape("""{"a":"text:Aa"}""", """{"a":"hello"}"""));
        Assert.Null(Shape("""{"a":"date:dd/0d"}""", """{"a":"01/02"}"""));
        Assert.Null(Shape("""{"a":"num:0"}""", """{"a":12}"""));
        Assert.Null(Shape("""{"a":"num:0"}""", """{"a":"12,5"}"""));
        Assert.Null(Shape("""{"a":"bool"}""", """{"a":false}"""));
        Assert.Null(Shape("""{"a":null}""", """{"a":null}"""));
        Assert.Null(Shape("""{"a":"anything"}""", """{"a":"s"}"""));
        Assert.Equal("effect GET /api/exports/7: a is a number, expected text", Shape("""{"a":"text:Aa"}""", """{"a":5}"""));
        Assert.Equal("effect GET /api/exports/7: a is a boolean, expected number or string", Shape("""{"a":"num:0"}""", """{"a":true}"""));
        Assert.Equal("effect GET /api/exports/7: a is a string, expected boolean", Shape("""{"a":"bool"}""", """{"a":"yes"}"""));
        Assert.Equal("effect GET /api/exports/7: a is a string, expected null", Shape("""{"a":null}""", """{"a":"x"}"""));
        Assert.Equal("effect GET /api/exports/7: a is null, expected string", Shape("""{"a":"anything"}""", """{"a":null}"""));
        Assert.Equal("effect GET /api/exports/7: a is an object, expected date", Shape("""{"a":"date:x"}""", """{"a":{}}"""));
    }

    [Fact]
    public void An_array_is_checked_by_its_first_element_and_an_empty_actual_is_a_mismatch()
    {
        Assert.Null(Shape("""{"rows":[{"a":"text:x"}]}""", """{"rows":[{"a":"s"},{"a":5}]}"""));
        Assert.Equal("effect GET /api/exports/7: rows is empty", Shape("""{"rows":[{"a":"text:x"}]}""", """{"rows":[]}"""));
        Assert.Null(Shape("""{"rows":[]}""", """{"rows":[]}"""));
        Assert.Equal("effect GET /api/exports/7: rows is an object, expected array", Shape("""{"rows":[]}""", """{"rows":{}}"""));
        Assert.Equal("effect GET /api/exports/7: rows[0].a is a number, expected text", Shape("""{"rows":[{"a":"text:x"}]}""", """{"rows":[{"a":3}]}"""));
        Assert.Null(Shape("""["text:x"]""", """["a"]"""));
    }

    [Fact]
    public void Messages_name_the_method_path_and_kind_and_never_a_value()
    {
        var m = Shape("""{"rows":[{"cedant":"text:Aa"}]}""", """{"rows":[{"cedant":424242}]}""")!;
        Assert.DoesNotContain("424242", m);
        var s = Shape("""{"a":"bool"}""", """{"a":"top-secret"}""")!;
        Assert.DoesNotContain("top-secret", s);
    }

    [Fact]
    public void Path_params_are_filled_from_param_and_a_missing_one_is_named()
    {
        Assert.Equal(("/api/exports/7", null), EffectReadBack.FillPath("/api/exports/:id", Id7));
        Assert.Equal(("/a/7/b/x", null), EffectReadBack.FillPath("/a/:id/b/:k", new Dictionary<string, string> { ["id"] = "7", ["k"] = "x" }));
        Assert.Equal(("/no/params", null), EffectReadBack.FillPath("/no/params", Id7));
        Assert.Equal((null, "k"), EffectReadBack.FillPath("/a/:id/b/:k", Id7));
    }

    [Fact]
    public void Prepare_refuses_a_method_that_is_not_read_only_and_needs_every_param()
    {
        var post = EffectReadBack.Prepare(Effect("POST"), Id7);
        Assert.Equal(new EffectResult("fail", "effect POST /api/exports/:id is not read-only"), post.Early);

        var missing = EffectReadBack.Prepare(Effect(), new Dictionary<string, string>());
        Assert.Equal(new EffectResult("could_not_run", "effect /api/exports/:id needs --param id"), missing.Early);

        var ok = EffectReadBack.Prepare(Effect(), Id7);
        Assert.Null(ok.Early);
        Assert.Equal("/api/exports/7", ok.Path);
        Assert.Null(EffectReadBack.Prepare(Effect("head"), Id7).Early);
    }

    [Fact]
    public void Judge_checks_status_then_shape_and_head_checks_status_only()
    {
        var e = Effect(shape: """{"rows":[{"cedant":"text:Aa"}]}""");
        Assert.Equal("pass", EffectReadBack.Judge(e, "/api/exports/7", 200, J("""{"rows":[{"cedant":"Acme"}]}""")).Outcome);
        Assert.Equal(new EffectResult("fail", "effect GET /api/exports/7: status 500, expected 200"), EffectReadBack.Judge(e, "/api/exports/7", 500, null));
        Assert.Equal(new EffectResult("fail", "effect GET /api/exports/7: rows[0].cedant is a number, expected text"),
            EffectReadBack.Judge(e, "/api/exports/7", 200, J("""{"rows":[{"cedant":1}]}""")));
        Assert.Equal("fail", EffectReadBack.Judge(e, "/api/exports/7", 200, null).Outcome);
        Assert.Equal("pass", EffectReadBack.Judge(Effect("HEAD", shape: """{"a":"x"}"""), "/p", 200, null).Outcome);
        Assert.Equal("pass", EffectReadBack.Judge(Effect(shape: null), "/p", 200, null).Outcome);
    }

    [Fact]
    public void The_expanded_step_count_adds_the_entry_action_only_when_there_is_no_marker()
    {
        Assert.Equal(3, EffectReadBack.ExpandedStepCount(new FeatureJourney("j", 2, false, null), true));
        Assert.Equal(2, EffectReadBack.ExpandedStepCount(new FeatureJourney("j", 2, true, null), true));
        Assert.Equal(2, EffectReadBack.ExpandedStepCount(new FeatureJourney("j", 2, false, null), false));
        // designc drops the marker when there is no entry point to put in its place.
        Assert.Equal(1, EffectReadBack.ExpandedStepCount(new FeatureJourney("j", 2, true, null), false));
    }
}
