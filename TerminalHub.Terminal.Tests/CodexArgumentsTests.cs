using TerminalHub.Constants;
using TerminalHub.Models;
using Xunit;

namespace TerminalHub.Terminal.Tests;

public sealed class CodexArgumentsTests
{
    [Fact]
    public void RecommendedPreset_UsesDocumentedOverridesBeforeResume()
    {
        var options = new Dictionary<string, string>
        {
            ["permission-preset"] = "recommended",
            ["resume-last"] = "true"
        };

        var actual = TerminalConstants.BuildCodexArgs(options);

        Assert.Equal(
            "--sandbox workspace-write --ask-for-approval on-request " +
            "-c approvals_reviewer=auto_review -c windows.sandbox=elevated " +
            "-c sandbox_workspace_write.network_access=true --search resume --last",
            actual);
    }

    [Fact]
    public void AskForApprovalPreset_UsesUserAsReviewer()
    {
        var options = new Dictionary<string, string>
        {
            ["permission-preset"] = "ask-for-approval",
            ["sandbox-mode"] = "read-only",
            ["ask-for-approval"] = "never",
            ["approvals-reviewer"] = "auto_review",
            ["windows-sandbox"] = "unelevated",
            ["network-access"] = "false",
            ["web-search-mode"] = "disabled"
        };

        Assert.Equal(
            "--sandbox workspace-write --ask-for-approval on-request -c approvals_reviewer=user -c windows.sandbox=elevated -c sandbox_workspace_write.network_access=true --search",
            TerminalConstants.BuildCodexArgs(options));
    }

    [Fact]
    public void PermissionRequest_UsesPresetReviewer()
    {
        Assert.False(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string> { ["permission-preset"] = "recommended" }));
        Assert.True(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string> { ["permission-preset"] = "ask-for-approval" }));
        Assert.False(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string>
            {
                ["permission-preset"] = "custom",
                ["approvals-reviewer"] = "auto_review"
            }));
        Assert.True(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string>()));
    }

    [Fact]
    public void PermissionRequest_UsesLastExplicitReviewerOverride()
    {
        Assert.True(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string>
            {
                ["permission-preset"] = "recommended",
                ["extra-args"] = "-c approvals_reviewer=user"
            }));
        Assert.False(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string>
            {
                ["permission-preset"] = "ask-for-approval",
                ["extra-args"] = "-c approvals_reviewer=user",
                ["custom-args"] = "-c \"approvals_reviewer=auto_review\""
            }));
    }

    [Fact]
    public void PermissionRequest_DoesNotWaitWhenApprovalsAreDisabled()
    {
        Assert.False(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string> { ["mode"] = "yolo" }));
        Assert.False(TerminalConstants.CodexPermissionRequestRequiresUserInput(
            new Dictionary<string, string>
            {
                ["permission-preset"] = "custom",
                ["ask-for-approval"] = "on-request",
                ["extra-args"] = "--ask-for-approval=never"
            }));
    }

    [Fact]
    public void PermissionRequest_ProcessSnapshotDoesNotChangeUntilRestart()
    {
        var launchOptions = new Dictionary<string, string>
        {
            ["permission-preset"] = "ask-for-approval"
        };
        var runningProcess = CodexProcessOptionsSnapshot.Capture(launchOptions);

        var savedWithoutRestart = new Dictionary<string, string>
        {
            ["permission-preset"] = "recommended"
        };

        Assert.True(CodexProcessOptionsSnapshot.ResolvePermissionRequestRequiresUserInput(
            runningProcess, savedWithoutRestart));
        Assert.False(TerminalConstants.CodexPermissionRequestRequiresUserInput(savedWithoutRestart));
    }

    [Fact]
    public void LegacyCodexDefaultPreset_DropsStalePermissionOverrides()
    {
        var options = new Dictionary<string, string>
        {
            ["permission-preset"] = "codex-default",
            ["sandbox-mode"] = "danger-full-access",
            ["ask-for-approval"] = "never",
            ["windows-sandbox"] = "elevated",
            ["network-access"] = "true",
            ["search"] = "true",
            ["resume-last"] = "true"
        };

        Assert.Equal("resume --last", TerminalConstants.BuildCodexArgs(options));
    }

    [Fact]
    public void CustomPreset_EmitsExplicitOffValues()
    {
        var options = new Dictionary<string, string>
        {
            ["permission-preset"] = "custom",
            ["sandbox-mode"] = "workspace-write",
            ["ask-for-approval"] = "on-request",
            ["approvals-reviewer"] = "user",
            ["windows-sandbox"] = "unelevated",
            ["network-access"] = "false",
            ["web-search-mode"] = "disabled",
            ["resume-last"] = "true"
        };

        var actual = TerminalConstants.BuildCodexArgs(options);

        Assert.Equal(
            "--sandbox workspace-write --ask-for-approval on-request " +
            "-c approvals_reviewer=user -c windows.sandbox=unelevated " +
            "-c sandbox_workspace_write.network_access=false -c web_search=disabled resume --last",
            actual);
    }

    [Fact]
    public void Yolo_IgnoresNormalPermissionPreset()
    {
        var options = new Dictionary<string, string>
        {
            ["mode"] = "yolo",
            ["permission-preset"] = "custom",
            ["sandbox-mode"] = "workspace-write",
            ["ask-for-approval"] = "on-request",
            ["approvals-reviewer"] = "user",
            ["windows-sandbox"] = "elevated",
            ["network-access"] = "true",
            ["web-search-mode"] = "live",
            ["resume-last"] = "true"
        };

        Assert.Equal(
            "--dangerously-bypass-approvals-and-sandbox resume --last",
            TerminalConstants.BuildCodexArgs(options));
    }

    [Fact]
    public void RecommendedPreset_DoesNotDuplicateUserSuppliedOverrides()
    {
        var options = new Dictionary<string, string>
        {
            ["permission-preset"] = "recommended",
            ["extra-args"] = "-c windows.sandbox=unelevated --search",
            ["custom-args"] = "-c approvals_reviewer=user -c sandbox_workspace_write.network_access=false"
        };

        var actual = TerminalConstants.BuildCodexArgs(options);

        Assert.Equal(
            "--sandbox workspace-write --ask-for-approval on-request " +
            "-c windows.sandbox=unelevated --search " +
            "-c approvals_reviewer=user -c sandbox_workspace_write.network_access=false",
            actual);
    }

    [Fact]
    public void CustomPreset_DoesNotAddSearchWhenWebSearchOverrideIsUserSupplied()
    {
        var options = new Dictionary<string, string>
        {
            ["permission-preset"] = "custom",
            ["web-search-mode"] = "live",
            ["extra-args"] = "-c web_search=disabled"
        };

        Assert.Equal(
            "-c web_search=disabled",
            TerminalConstants.BuildCodexArgs(options));
    }

    [Fact]
    public void RecommendedPreset_DoesNotDuplicateUserSuppliedNonRepeatableOptions()
    {
        var options = new Dictionary<string, string>
        {
            ["permission-preset"] = "recommended",
            ["extra-args"] = "--sandbox=read-only -a never"
        };

        var actual = TerminalConstants.BuildCodexArgs(options);

        Assert.Equal(
            "-c approvals_reviewer=auto_review -c windows.sandbox=elevated " +
            "-c sandbox_workspace_write.network_access=true --search --sandbox=read-only -a never",
            actual);
    }

    [Fact]
    public void Yolo_DoesNotDuplicateUserSuppliedDangerousBypass()
    {
        var options = new Dictionary<string, string>
        {
            ["mode"] = "yolo",
            ["extra-args"] = "--dangerously-bypass-approvals-and-sandbox",
            ["resume-last"] = "true"
        };

        Assert.Equal(
            "--dangerously-bypass-approvals-and-sandbox resume --last",
            TerminalConstants.BuildCodexArgs(options));
    }
}
