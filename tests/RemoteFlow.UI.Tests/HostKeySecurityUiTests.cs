using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Security;
using RemoteFlow.UI.Views.Security;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class HostKeySecurityUiTests
{
    [AvaloniaFact]
    public void MismatchDefaultsEnterAndEscapeToRejectAndRequiresTwoAcceptanceClicks()
    {
        var prompt = new HostKeyTrustPrompt(
            "critical.example",
            22,
            "ssh-ed25519",
            "SHA256:offered",
            "SHA256:stored",
            "+---+");
        var dialog = new HostKeyPromptWindow(prompt);
        var buttons = dialog.GetLogicalDescendants().OfType<Button>().ToArray();
        var reject = Assert.Single(buttons, button => Equals(button.Content, "Reject"));
        var accept = Assert.Single(buttons, button => Equals(button.Content, "Accept and save"));

        Assert.True(reject.IsDefault);
        Assert.True(reject.IsCancel);
        dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(HostKeyPromptDecision.Reject, dialog.Decision);

        var acceptanceDialog = new HostKeyPromptWindow(prompt);
        accept = Assert.Single(
            acceptanceDialog.GetLogicalDescendants().OfType<Button>(),
            button => Equals(button.Content, "Accept and save"));
        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(HostKeyPromptDecision.Reject, acceptanceDialog.Decision);
        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(HostKeyPromptDecision.AcceptAndSave, acceptanceDialog.Decision);
    }

    [AvaloniaFact]
    public void KeyboardInteractiveUsesServerTextAndHonorsEchoFlags()
    {
        var dialog = new KeyboardInteractivePromptWindow(
        [
            new SshAuthenticationPrompt("One-time password", IsSecret: true),
            new SshAuthenticationPrompt("Account label", IsSecret: false),
        ]);
        var inputs = dialog.GetLogicalDescendants().OfType<TextBox>().ToArray();

        Assert.Equal(2, inputs.Length);
        Assert.NotEqual(default, inputs[0].PasswordChar);
        Assert.Equal(default, inputs[1].PasswordChar);
    }

    [Fact]
    public async Task KnownHostsImportPreviewsBeforeApplyAndNeverWritesSource()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("remoteflow-known-hosts-");
        try
        {
            var path = Path.Combine(directory.FullName, "known_hosts");
            var salt = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
#pragma warning disable CA5350 // Generates an OpenSSH version-1 hashed hostname fixture.
            using var hmac = new HMACSHA1(salt);
#pragma warning restore CA5350
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes("secret.example"));
            var hashedHost = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
            var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            var original = $"plain.example ssh-ed25519 {key} plain{Environment.NewLine}{hashedHost} ssh-ed25519 {key}";
            await File.WriteAllTextAsync(path, original, token);
            var originalTime = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(path, originalTime);
            originalTime = File.GetLastWriteTimeUtc(path);

            var store = new InMemoryHostKeyStore();
            var service = new KnownHostsImportService(store, new FakeGuidProvider(), new FakeClock(DateTimeOffset.UtcNow));
            var preview = await service.PreviewAsync(path, token);

            Assert.Equal(2, preview.Entries.Count);
            Assert.Empty(await store.ListAsync(token));
            var hashed = Assert.Single(preview.Entries, entry => entry.IsHashed);
            Assert.Contains("Hashed", hashed.DisplayHost, StringComparison.Ordinal);

            var result = await service.ApplyAsync(preview, token);

            Assert.Equal(2, result.Added);
            Assert.Equal(original, await File.ReadAllTextAsync(path, token));
            Assert.Equal(originalTime, File.GetLastWriteTimeUtc(path));
            Assert.Contains(await store.ListAsync(token), item => item.Host == hashedHost);
            Assert.True(KnownHostsHash.Matches(hashedHost, "secret.example", 22));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RevokingFromManagementScreenMakesVerifierRefuseNextConnection()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryHostKeyStore();
        var publicKey = new byte[] { 1, 2, 3, 4 };
        var key = HostKey.Create(
            new FakeGuidProvider(),
            "server.test",
            22,
            "ssh-ed25519",
            Convert.ToBase64String(publicKey),
            HostKeyFingerprint.FormatSha256(publicKey),
            HostKeyTrust.Trusted,
            HostKeySource.UserAccepted).Value;
        await store.AddAsync(key, token);
        var importer = new KnownHostsImportService(store, new FakeGuidProvider(), new FakeClock(DateTimeOffset.UtcNow));
        var viewModel = new TrustedKeysViewModel(store, importer, new AlwaysConfirm());
        await viewModel.LoadAsync(token);

        await viewModel.RevokeCommand.ExecuteAsync(Assert.Single(viewModel.Keys));
        var verifier = new HostKeyVerifier(
            store,
            new RejectingPrompt(),
            new FakeClock(DateTimeOffset.UtcNow),
            new FakeGuidProvider());
        var result = await verifier.VerifyAsync(new(
            "server.test",
            22,
            new HostKeyInfo("ssh-ed25519", publicKey, "ignored"),
            HostKeyPolicy.TrustOnFirstUse), token);

        Assert.Equal(SshError.HostKeyRevoked, result.Failure.Error);
    }

    private sealed class AlwaysConfirm : IConfirmationDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class RejectingPrompt : IHostKeyPrompt
    {
        public ValueTask<bool> ConfirmTrustAsync(HostKeyTrustPrompt prompt, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(false);
        }
    }
}
