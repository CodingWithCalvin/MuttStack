namespace Scout.Core;

public enum AssignmentIssue { NonConsecutiveEpisodes, OverlappingStories, PromotionalContent, IncompleteCombinedStory, UncertainStoryRole, CoverageMismatch, MissingDescription }

/// <summary>Content/identity failures must be held, not repaired by prompting for a different episode number.</summary>
public static class AssignmentValidation
{
    public const string ReasonKey="assignment_validation";
    public static InvalidDataException Reject(AssignmentIssue issue,string message) {
        var error=new InvalidDataException(message);error.Data[ReasonKey]=issue.ToString();return error;
    }
    public static bool IsTerminal(InvalidDataException error)=>error.Data.Contains(ReasonKey);
}
