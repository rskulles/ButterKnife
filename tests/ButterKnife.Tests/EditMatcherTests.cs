using ButterKnife.Services;

namespace ButterKnife.Tests;

public class EditMatcherTests
{
    private const string Draw = """
        void draw() {
                // Gems
                for (auto& g : gems) {
                    DrawTexturePro(spritesheet, gemRect, {g.x - 8, g.y - 8, 16, 16}, {0,0}, 0, WHITE);
                }
                
                // Particles
                for (auto& p : particles) DrawCircleV(p.pos, 2, Fade(p.color, p.life));
        }

        """;

    [Fact]
    public void ExactMatchesNeedNoNote()
    {
        var plan = EditMatcher.Plan(Draw, "// Particles", "// Sparks", replaceAll: false, "main.cpp");

        Assert.Contains("// Sparks", plan.Updated);
        Assert.Null(plan.Note);
    }

    [Fact]
    public void LineNumbersCopiedFromReadFileAreRemoved()
    {
        var plan = EditMatcher.Plan(Draw, "     7\t        // Particles\n     8\t        for", "     7\t        // Sparks\n     8\t        for", replaceAll: false, "main.cpp");

        Assert.Contains("        // Sparks\n        for", plan.Updated);
        Assert.Equal(EditMatcher.NumbersNote, plan.Note);
    }

    [Fact]
    public void TrailingSpacesOnBlankLinesDoNotMatter()
    {
        // The file's blank line holds eight spaces; the model wrote an empty one.
        var old = "        }\n\n        // Particles";

        var plan = EditMatcher.Plan(Draw, old, "        }\n\n        // Sparks", replaceAll: false, "main.cpp");

        Assert.Contains("        }\n\n        // Sparks\n", plan.Updated);
        Assert.Equal(EditMatcher.LooseNote, plan.Note);
    }

    [Fact]
    public void ABlockIndentedDifferentlyIsMatchedAndNewStringReindented()
    {
        var old = "for (auto& g : gems) {\n    DrawTexturePro(spritesheet, gemRect, {g.x - 8, g.y - 8, 16, 16}, {0,0}, 0, WHITE);\n}";
        var replacement = "for (auto& g : gems) {\n    DrawGem(g);\n}";

        var plan = EditMatcher.Plan(Draw, old, replacement, replaceAll: false, "main.cpp");

        Assert.Contains("        for (auto& g : gems) {\n            DrawGem(g);\n        }\n", plan.Updated);
    }

    [Fact]
    public void TabsInTheFileReplaceTheModelsSpaces()
    {
        var file = "int f() {\n\treturn 1;\n}\n";

        var plan = EditMatcher.Plan(file, "    return 1;", "    return 2;", replaceAll: false, "f.c");

        Assert.Equal("int f() {\n\treturn 2;\n}\n", plan.Updated);
    }

    [Fact]
    public void LooseMatchesMustBeUniqueToo()
    {
        var file = "a {\n  x;\n}\nb {\n    x;\n}\n";

        var error = Assert.Throws<CodeToolException>(() => EditMatcher.Plan(file, "x;", "y;", replaceAll: false, "f.c"));

        Assert.Contains("occurs 2 times", error.Message);
        Assert.Equal("a {\n  y;\n}\nb {\n    y;\n}\n", EditMatcher.Plan(file, "x;", "y;", replaceAll: true, "f.c").Updated);
    }

    [Fact]
    public void WindowsLineBreaksSurviveALooseMatch()
    {
        var file = "a\r\n    b\r\nc";

        var plan = EditMatcher.Plan(file, "b\nc", "B\nC", replaceAll: false, "f.txt");

        Assert.Equal("a\r\n    B\r\n    C", plan.Updated);
    }

    [Fact]
    public void TextFromMemoryGetsTheRealLinesBack()
    {
        // What the model sent (the failure that prompted this): its own idea of a line, not the file's.
        var old = """
                    // Gems
                    for (auto& g : gems) {
                        DrawTexturePro(spritesheet, gemRect, {g.x, g.y, 16, 16}, {8, 8}, 0, WHITE);
                    }
            """;

        var error = Assert.Throws<CodeToolException>(() => EditMatcher.Plan(Draw, old, "x", replaceAll: false, "main.cpp"));

        Assert.Contains("The closest text is at lines 2–5, where 3 of its 4 non-blank lines match.", error.Message);
        Assert.Contains("The first difference is at line 4:", error.Message);
        Assert.Contains("old_string has: DrawTexturePro(spritesheet, gemRect, {g.x, g.y, 16, 16}, {8, 8}, 0, WHITE);", error.Message);
        Assert.Contains("the file has:   DrawTexturePro(spritesheet, gemRect, {g.x - 8, g.y - 8, 16, 16}, {0,0}, 0, WHITE);", error.Message);
        Assert.Contains("     4\t            DrawTexturePro(spritesheet, gemRect, {g.x - 8", error.Message); // numbered, to copy from
    }

    [Fact]
    public void TextThatIsNowhereSaysSo()
    {
        var error = Assert.Throws<CodeToolException>(() => EditMatcher.Plan(Draw, "int main() {}", "x", replaceAll: false, "main.cpp"));

        Assert.Contains("None of its lines are in the file", error.Message);
    }
}
