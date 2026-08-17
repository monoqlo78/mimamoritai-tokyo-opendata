using MimamoriTai.Core.Application;

namespace MimamoriTai.Tests;

/// <summary>
/// The deterministic layers answer without a model, so when one of them claims a message
/// it should not have, the user gets a confident wrong answer and nothing downstream can
/// correct it. Every case here was measured going wrong in docs/eval/intent-accuracy.md.
/// </summary>
public class DeterministicLayerBoundaryTests
{
    [Theory]
    [InlineData("施設の費用の相場はいくら")]
    [InlineData("介護の費用はいくらかかりますか")]
    [InlineData("入院の費用が心配です")]
    [InlineData("今月の電気料金はいくら")]
    public void CostFaqDoesNotClaimMoneyQuestionsAboutTheResidentsLife(string message)
    {
        Assert.Null(AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict));
    }

    [Theory]
    [InlineData("料金はかかりますか")]
    [InlineData("これは有料ですか")]
    [InlineData("お金はかかるの")]
    public void CostFaqStillAnswersQuestionsAboutTheServiceItself(string message)
    {
        var answer = AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict);

        Assert.NotNull(answer);
        Assert.Equal("cost", answer.Id);
    }

    [Theory]
    [InlineData("最近は電気の使い方が増えている？")]
    [InlineData("お金の使い方が変わってきた")]
    public void HelpFaqDoesNotClaimQuestionsAboutHowTheResidentLives(string message)
    {
        Assert.Null(AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict));
    }

    [Theory]
    [InlineData("使い方")]
    [InlineData("使い方を教えて")]
    [InlineData("何ができるの")]
    public void HelpFaqStillAnswersProductHelp(string message)
    {
        var answer = AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict);

        Assert.NotNull(answer);
        Assert.Equal("what-is-this", answer.Id);
    }

    [Theory]
    [InlineData("施設の費用の相場はいくら")]
    [InlineData("施設に入るにはどうすればいい")]
    public void CareFacilityQuestionsGoToAProfessional(string message)
    {
        var referral = AssistantExpertGuidance.TryRefer(message);

        Assert.NotNull(referral);
        Assert.Equal(ExpertField.Care, referral.Field);
    }

    [Fact]
    public void MentioningAFacilityWithoutAskingIsStillConversation()
    {
        // 「施設に行ってきたよ」 is the resident telling us about their day. Answering that
        // with a referral turns the one moment of contact they started into a rebuff.
        Assert.Null(AssistantExpertGuidance.TryRefer("施設に行ってきたよ"));
    }
}
