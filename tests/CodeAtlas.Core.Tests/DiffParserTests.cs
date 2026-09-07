using CodeAtlas.Core.Git;

namespace CodeAtlas.Core.Tests;

public class DiffParserTests
{
    private const string TwoFiles = """
        diff --git a/src/Widget.cs b/src/Widget.cs
        index 1111111..2222222 100644
        --- a/src/Widget.cs
        +++ b/src/Widget.cs
        @@ -8,7 +8,8 @@ namespace App;
             public void Run()
             {
        -        Legacy();
        +        Prepare();
        +        Execute();
             }
         }
        diff --git a/README.md b/README.md
        index 3333333..4444444 100644
        --- a/README.md
        +++ b/README.md
        @@ -1 +1 @@
        -old
        +new
        """;

    [Fact]
    public void Splits_a_diff_into_one_entry_per_file()
    {
        var files = DiffParser.Parse(TwoFiles);

        Assert.Equal(["src/Widget.cs", "README.md"], files.Select(file => file.Path));
    }

    [Fact]
    public void Reads_the_hunk_header()
    {
        var hunk = Assert.Single(DiffParser.Parse(TwoFiles)[0].Hunks);

        Assert.Equal(8, hunk.OldStart);
        Assert.Equal(7, hunk.OldCount);
        Assert.Equal(8, hunk.NewStart);
        Assert.Equal(8, hunk.NewCount);
    }

    /// <summary>
    /// The line numbers are derived by walking the body, so context advances the cursor
    /// and only the two added lines are reported as added.
    /// </summary>
    [Fact]
    public void Numbers_added_lines_on_the_new_side()
    {
        var hunk = Assert.Single(DiffParser.Parse(TwoFiles)[0].Hunks);

        Assert.Equal([10, 11], hunk.AddedLines.Order());
    }

    /// <summary>
    /// A removal has no new-side line of its own, so it lands on the line that now sits
    /// where it was — which is what keeps a deletion inside the declaration it came from.
    /// </summary>
    [Fact]
    public void Attributes_a_removal_to_the_line_that_replaced_it()
    {
        var hunk = Assert.Single(DiffParser.Parse(TwoFiles)[0].Hunks);

        Assert.Equal(1, hunk.RemovedLineCount);
        Assert.True(hunk.HasRemovals);
        Assert.Contains(10, hunk.TouchedLines);
    }

    [Fact]
    public void Keeps_the_files_own_text_for_display()
    {
        var file = DiffParser.Parse(TwoFiles)[1];

        Assert.Contains("+new", file.Text);
        Assert.DoesNotContain("Widget", file.Text);
    }

    [Fact]
    public void Reads_a_hunk_whose_count_is_implicit()
    {
        var hunk = Assert.Single(DiffParser.Parse(TwoFiles)[1].Hunks);

        Assert.Equal(1, hunk.OldCount);
        Assert.Equal(1, hunk.NewCount);
        Assert.Equal([1], hunk.TouchedLines);
    }

    /// <summary>
    /// A pure deletion reports a new-side start of the line before it, and there is no
    /// line 0 to attribute a removal at the very top of a file to.
    /// </summary>
    [Fact]
    public void Clamps_a_removal_at_the_top_of_a_file_to_line_one()
    {
        var diff = """
            diff --git a/src/Widget.cs b/src/Widget.cs
            --- a/src/Widget.cs
            +++ b/src/Widget.cs
            @@ -1,2 +0,0 @@
            -first
            -second
            """;

        var hunk = Assert.Single(DiffParser.Parse(diff).Single().Hunks);

        Assert.Equal([1], hunk.TouchedLines);
        Assert.Equal(2, hunk.RemovedLineCount);
        Assert.Empty(hunk.AddedLines);
    }

    [Fact]
    public void Names_a_deleted_file_from_its_old_path()
    {
        var diff = """
            diff --git a/src/Gone.cs b/src/Gone.cs
            deleted file mode 100644
            --- a/src/Gone.cs
            +++ /dev/null
            @@ -1,3 +0,0 @@
            -namespace App;
            -
            -public class Gone;
            """;

        var file = Assert.Single(DiffParser.Parse(diff));

        Assert.Equal("src/Gone.cs", file.Path);
    }

    [Fact]
    public void Records_a_rename_with_the_path_it_came_from()
    {
        var diff = """
            diff --git a/src/Old.cs b/src/New.cs
            similarity index 92%
            rename from src/Old.cs
            rename to src/New.cs
            --- a/src/Old.cs
            +++ b/src/New.cs
            @@ -1,2 +1,2 @@
             namespace App;
            -public class Old;
            +public class New;
            """;

        var file = Assert.Single(DiffParser.Parse(diff));

        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal("src/Old.cs", file.OldPath);
    }

    [Fact]
    public void Flags_a_binary_difference_and_maps_nothing_from_it()
    {
        var diff = """
            diff --git a/logo.png b/logo.png
            index 5555555..6666666 100644
            Binary files a/logo.png and b/logo.png differ
            """;

        var file = Assert.Single(DiffParser.Parse(diff));

        Assert.True(file.IsBinary);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void Ignores_the_no_newline_marker()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file
            """;

        var hunk = Assert.Single(DiffParser.Parse(diff).Single().Hunks);

        Assert.Equal([1], hunk.AddedLines.Order());
        Assert.Equal(1, hunk.RemovedLineCount);
    }

    [Fact]
    public void Reports_every_touched_line_across_hunks_once_and_in_order()
    {
        var diff = """
            diff --git a/src/Widget.cs b/src/Widget.cs
            --- a/src/Widget.cs
            +++ b/src/Widget.cs
            @@ -1,1 +1,2 @@
             one
            +two
            @@ -20,1 +21,2 @@
             twenty
            +twentyone
            """;

        var file = Assert.Single(DiffParser.Parse(diff));

        Assert.Equal([2, 22], file.TouchedLines);
    }

    /// <summary>
    /// An added line whose own content starts with <c>++</c> is written as
    /// <c>+++counter;</c>, which looks exactly like a header line. It is a real change and
    /// has to be counted as one.
    /// </summary>
    [Fact]
    public void Counts_a_changed_line_whose_content_looks_like_a_diff_header()
    {
        var diff = """
            diff --git a/src/Widget.cs b/src/Widget.cs
            --- a/src/Widget.cs
            +++ b/src/Widget.cs
            @@ -4,1 +4,1 @@
            ---counter;
            +++counter;
            """;

        var hunk = Assert.Single(DiffParser.Parse(diff).Single().Hunks);

        Assert.Equal([4], hunk.AddedLines.Order());
        Assert.Equal(1, hunk.RemovedLineCount);
        Assert.Equal([4], hunk.TouchedLines);
    }

    [Fact]
    public void Parses_an_empty_diff_as_no_files()
    {
        Assert.Empty(DiffParser.Parse(string.Empty));
        Assert.Empty(DiffParser.Parse("\n"));
    }
}
