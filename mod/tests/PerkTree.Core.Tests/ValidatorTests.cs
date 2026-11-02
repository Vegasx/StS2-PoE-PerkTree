using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class ValidatorTests
{
    private static ValidationReport Validate(TreeDef t, EffectCatalog c, bool keywords = true) =>
        TreeValidator.Validate(t, c, keywords ? Fixture.Keywords : null);

    [Fact]
    public void Sample_data_is_valid_with_no_issues()
    {
        var report = Validate(Fixture.Tree(), Fixture.Catalog());
        Assert.True(report.IsValid, report.ToString());
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Unreachable_node_is_an_error()
    {
        var tree = Fixture.Tree();
        tree.Nodes.Add(Fixture.Node("island", "sample.discard_chance", 0.1m));
        Assert.True(Validate(tree, Fixture.Catalog()).Has("NODE_UNREACHABLE"));
    }

    [Fact]
    public void Node_with_unknown_effect_is_an_error()
    {
        var tree = Fixture.Tree();
        tree.Nodes.Add(Fixture.Node("ghost", "no.such.effect", 1));
        tree.Edges.Add(new[] { "util_3", "ghost" });
        Assert.True(Validate(tree, Fixture.Catalog()).Has("NODE_EFFECT_UNKNOWN"));
    }

    [Fact]
    public void Unknown_keyword_is_an_error_but_skipped_when_no_keyword_list_given()
    {
        var catalog = Fixture.Catalog();
        catalog.Effects.Add(new EffectDef { Id = "sample.x", Scope = EffectScope.Self, Keywords = { "NotARealKeyword" } });

        Assert.True(Validate(Fixture.Tree(), catalog).Has("EFFECT_KEYWORD_UNKNOWN"));
        Assert.False(Validate(Fixture.Tree(), catalog, keywords: false).Has("EFFECT_KEYWORD_UNKNOWN"));
    }

    [Fact]
    public void Effect_without_scope_is_an_error()
    {
        var catalog = Fixture.Catalog();
        catalog.Effects.Add(new EffectDef { Id = "sample.unclassified" });
        Assert.True(Validate(Fixture.Tree(), catalog).Has("EFFECT_NO_SCOPE"));
    }

    [Fact]
    public void Non_positive_cap_is_an_error()
    {
        var catalog = Fixture.Catalog();
        catalog.Effects.Add(new EffectDef { Id = "sample.zero_cap", Scope = EffectScope.Self, Cap = 0m });
        Assert.True(Validate(Fixture.Tree(), catalog).Has("EFFECT_BAD_CAP"));
    }

    [Fact]
    public void Duplicate_ids_are_errors()
    {
        var tree = Fixture.Tree();
        tree.Nodes.Add(Fixture.Node("util_1", "sample.discard_chance", 0.1m));
        var catalog = Fixture.Catalog();
        catalog.Effects.Add(new EffectDef { Id = "sample.discard_chance", Scope = EffectScope.Self });

        var report = Validate(tree, catalog);
        Assert.True(report.Has("NODE_DUPLICATE"));
        Assert.True(report.Has("EFFECT_DUPLICATE"));
    }

    [Fact]
    public void Bad_edges_are_errors_and_duplicate_edges_only_warn()
    {
        var tree = Fixture.Tree();
        tree.Edges.Add(new[] { "util_1", "nowhere" });
        tree.Edges.Add(new[] { "util_1", "util_1" });
        tree.Edges.Add(new[] { "util_1" });
        tree.Edges.Add(new[] { "util_2", "util_1" }); // reverse of an existing edge

        var report = Validate(tree, Fixture.Catalog());
        Assert.True(report.Has("EDGE_UNKNOWN_NODE"));
        Assert.True(report.Has("EDGE_SELF"));
        Assert.True(report.Has("EDGE_MALFORMED"));
        Assert.Contains(report.Warnings, w => w.Code == "EDGE_DUPLICATE");
    }

    [Fact]
    public void Missing_or_unknown_start_is_an_error()
    {
        var none = Fixture.Tree();
        none.Starts.Clear();
        Assert.True(Validate(none, Fixture.Catalog()).Has("NO_START"));

        var bad = Fixture.Tree();
        bad.Starts.Add("not_a_node");
        Assert.True(Validate(bad, Fixture.Catalog()).Has("START_UNKNOWN"));
    }

    [Fact]
    public void Character_limited_effect_on_the_wrong_character_is_only_a_warning()
    {
        var tree = PerkJson.ParseTree(Fixture.TreeText().Replace("\"silent\"", "\"ironclad\""));
        var report = Validate(tree, Fixture.Catalog());
        Assert.True(report.Has("NODE_EFFECT_WRONG_CHARACTER"));
        Assert.True(report.IsValid);   // placed on purpose, so the tree still loads
    }

    [Fact]
    public void Zero_magnitude_and_unknown_branch_are_errors()
    {
        var tree = Fixture.Tree();
        tree.Nodes.Add(Fixture.Node("dud", "sample.discard_chance", 0m));
        tree.Nodes.Add(Fixture.Node("lost", "sample.discard_chance", 0.1m, branch: "no_branch"));
        tree.Edges.Add(new[] { "util_3", "dud" });
        tree.Edges.Add(new[] { "util_3", "lost" });

        var report = Validate(tree, Fixture.Catalog());
        Assert.True(report.Has("NODE_ZERO_MAGNITUDE"));
        Assert.True(report.Has("NODE_BRANCH_UNKNOWN"));
    }

    [Fact]
    public void Tree_total_above_cap_warns_but_stays_valid()
    {
        var tree = Fixture.Tree();
        tree.Nodes.Add(Fixture.Node("util_4", "sample.discard_chance", 0.25m));
        tree.Nodes.Add(Fixture.Node("util_5", "sample.discard_chance", 0.25m));
        tree.Edges.Add(new[] { "util_3", "util_4" });
        tree.Edges.Add(new[] { "util_4", "util_5" });

        var report = Validate(tree, Fixture.Catalog());
        Assert.True(report.IsValid, report.ToString());
        Assert.True(report.Has("CAP_EXCEEDED")); // 5 x 0.25 = 1.25 > 1.0
    }

    [Fact]
    public void Catalog_can_be_validated_without_a_tree()
    {
        var good = TreeValidator.ValidateCatalog(Fixture.Catalog(), Fixture.Keywords);
        Assert.True(good.IsValid, good.ToString());

        var catalog = Fixture.Catalog();
        catalog.Effects.Add(new EffectDef { Id = "x.no_scope" });
        catalog.Effects.Add(new EffectDef { Id = "x.bad_kw", Scope = EffectScope.Self, Keywords = { "Nope" } });
        catalog.Effects.Add(new EffectDef { Id = "x.bad_cap", Scope = EffectScope.Self, Cap = -1m });

        var bad = TreeValidator.ValidateCatalog(catalog, Fixture.Keywords);
        Assert.True(bad.Has("EFFECT_NO_SCOPE"));
        Assert.True(bad.Has("EFFECT_KEYWORD_UNKNOWN"));
        Assert.True(bad.Has("EFFECT_BAD_CAP"));
    }

    [Fact]
    public void Build_throws_with_the_report_when_data_is_invalid()
    {
        var tree = Fixture.Tree();
        tree.Starts.Clear();
        var ex = Assert.Throws<InvalidTreeException>(() => TreeGraph.Build(tree, Fixture.Catalog()));
        Assert.True(ex.Report.Has("NO_START"));
    }

    [Fact]
    public void Sample_files_round_trip_through_json()
    {
        var tree = Fixture.Tree();
        var again = PerkJson.ParseTree(PerkJson.Serialize(tree));
        Assert.Equal(tree.Nodes.Count, again.Nodes.Count);
        Assert.Equal(tree.Edges.Count, again.Edges.Count);
        Assert.Equal(EffectScope.SharedEnemy,
            PerkJson.ParseCatalog(PerkJson.Serialize(Fixture.Catalog())).Effects.Single(e => e.Id == "sample.enemy_vulnerable_bonus").Scope);
    }
}
