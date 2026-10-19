using ButterKnife.Services;

namespace ButterKnife.Tests;

public class DraftStoreTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();

    [Fact]
    public void KeepsTextAndAttachmentsPerUserAndKey()
    {
        var drafts = new DraftStore();
        var image = new ChatImage("image/png", [1]);
        var file = new ChatFile("a.txt", "text/plain", 1, "a");

        drafts.Save(Me, "chat:1", "half a thought", [image], [file]);

        var draft = drafts.Get(Me, "chat:1");
        Assert.Equal("half a thought", draft!.Text);
        Assert.Same(image, Assert.Single(draft.Images));
        Assert.Same(file, Assert.Single(draft.Files));
        Assert.Null(drafts.Get(Someone, "chat:1"));
        Assert.Null(drafts.Get(Me, "chat:2"));
    }

    [Fact]
    public void AnEmptyComposerForgetsTheDraft()
    {
        var drafts = new DraftStore();
        drafts.Save(Me, "code:new", "x", []);

        drafts.Save(Me, "code:new", "   ", []);

        Assert.Null(drafts.Get(Me, "code:new"));
    }

    [Fact]
    public void SavingTheSameDraftAgainKeepsTheStoredCopy()
    {
        var drafts = new DraftStore();
        var images = new List<ChatImage> { new("image/png", [1]) };
        drafts.Save(Me, "chat:1", "same", images);
        var first = drafts.Get(Me, "chat:1");

        drafts.Save(Me, "chat:1", "same", images);

        Assert.Same(first, drafts.Get(Me, "chat:1"));
        images.Clear(); // the page's list changing later does not change the stored draft
        Assert.Single(first!.Images);
    }

    [Fact]
    public void TheOldestDraftsGoFirstPastTheCap()
    {
        var drafts = new DraftStore();
        for (var i = 0; i <= DraftStore.MaxDrafts; i++)
        {
            drafts.Save(Me, $"chat:{i}", "x", []);
        }

        Assert.Null(drafts.Get(Me, "chat:0"));
        Assert.NotNull(drafts.Get(Me, $"chat:{DraftStore.MaxDrafts}"));
    }

    [Fact]
    public void ReturnLocationSkipsSettings()
    {
        var nav = new FakeNavigation("http://localhost/");
        var back = new ReturnLocation();

        nav.Go("http://localhost/code/42");
        back.Remember(nav);
        nav.Go("http://localhost/settings/connections");
        back.Remember(nav);

        Assert.Equal("code/42", back.Path);
        Assert.True(back.IsCode);
    }

    private sealed class FakeNavigation : Microsoft.AspNetCore.Components.NavigationManager
    {
        public FakeNavigation(string baseUri) => Initialize(baseUri, baseUri);

        public void Go(string uri) => Uri = uri;
    }
}
