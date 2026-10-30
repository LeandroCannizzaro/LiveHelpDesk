using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveHelpDesk.Tests;

public class SecretProtectorTests
{
    [Fact]
    public void ASecret_IsEncryptedAndComesBack()
    {
        var stored = SecretProtector.Protect("p@ssw0rd-àèì");

        Assert.StartsWith("enc:", stored);
        Assert.DoesNotContain("p@ssw0rd", stored);
        Assert.True(SecretProtector.IsProtected(stored));
        Assert.Equal("p@ssw0rd-àèì", SecretProtector.Reveal(stored));
    }

    [Fact]
    public void PlainText_PassesThroughReveal() =>
        Assert.Equal("typed by hand", SecretProtector.Reveal("typed by hand"));

    [Fact]
    public void TheSameSecret_IsNotEncryptedTheSameWayTwice() =>
        Assert.NotEqual(SecretProtector.Protect("x"), SecretProtector.Protect("x"));
}

public sealed class ChannelStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lhd-channels-{Guid.NewGuid():N}");
    private string File_ => Path.Combine(_directory, "channels.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ChannelStore Store() => new(File_, NullLogger.Instance);

    private static ChannelSettings Settings() => new()
    {
        Email = { Enabled = true, Host = "smtp.example.com", From = "helpdesk@example.com", To = ["a@example.com"], User = "helpdesk@example.com", Password = "hunter2" },
        Teams = { Enabled = true, Url = "https://prod.logic.azure.com/hook?sig=SECRETSIG" },
        Sms = { Enabled = true, AccountSid = "ACd0c0ffee0123456789", AuthToken = "twilio-token", From = "+390212345678", To = ["+393331234567"] },
    };

    [Fact]
    public void WithoutAFile_EveryOutsideChannelIsOff()
    {
        var current = Store().Current;

        Assert.False(current.Email.Enabled);
        Assert.False(current.Teams.Enabled);
        Assert.False(current.Sms.Enabled);
        Assert.False(current.WhatsApp.Enabled);
    }

    [Fact]
    public void Saved_ThenReopened_TheSettingsComeBack_ButNoSecretIsOnDisk()
    {
        Store().Save(Settings());

        var onDisk = File.ReadAllText(File_);
        Assert.DoesNotContain("hunter2", onDisk);
        Assert.DoesNotContain("SECRETSIG", onDisk);
        Assert.DoesNotContain("twilio-token", onDisk);
        Assert.Contains("enc:", onDisk);
        Assert.Contains("smtp.example.com", onDisk); // what is not secret stays readable

        var reopened = Store().Current;
        Assert.Equal("hunter2", reopened.Email.Password);
        Assert.Equal("https://prod.logic.azure.com/hook?sig=SECRETSIG", reopened.Teams.Url);
        Assert.Equal("twilio-token", reopened.Sms.AuthToken);
    }

    [Fact]
    public void ASecretTypedIntoTheFileByHand_IsEncryptedAtTheNextRead()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(File_, """{ "sms": { "enabled": true, "accountSid": "ACd0c0ffee0123456789", "authToken": "typed-by-hand", "from": "+390212345678", "to": ["+393331234567"] } }""");

        var store = Store();

        Assert.Equal("typed-by-hand", store.Current.Sms.AuthToken);
        var onDisk = File.ReadAllText(File_);
        Assert.DoesNotContain("typed-by-hand", onDisk);
        Assert.Contains("enc:", onDisk);
        Assert.Equal("typed-by-hand", Store().Current.Sms.AuthToken);
    }

    [Fact]
    public void WhatTheAppSees_HasEverySecretMasked()
    {
        var masked = ChannelStore.Masked(Settings());

        Assert.Equal(ChannelStore.Mask, masked.Email.Password);
        Assert.Equal(ChannelStore.Mask, masked.Teams.Url);
        Assert.Equal(ChannelStore.Mask, masked.Sms.AuthToken);
        Assert.Equal("", masked.WhatsApp.AuthToken); // nothing to hide
        Assert.Equal("smtp.example.com", masked.Email.Host);
        Assert.Equal("hunter2", Settings().Email.Password);
    }

    [Fact]
    public void SendingTheMaskBack_KeepsTheStoredSecret_AndANewValueReplacesIt()
    {
        var store = Store();
        store.Save(Settings());

        var edited = ChannelStore.Masked(store.Current);
        edited.Email.Host = "smtp2.example.com";
        store.Save(edited);

        Assert.Equal("smtp2.example.com", store.Current.Email.Host);
        Assert.Equal("hunter2", store.Current.Email.Password);

        edited.Email.Password = "new-password";
        edited.Teams.Url = ChannelStore.Mask;
        store.Save(edited);

        Assert.Equal("new-password", Store().Current.Email.Password);
        Assert.Equal("https://prod.logic.azure.com/hook?sig=SECRETSIG", Store().Current.Teams.Url);
    }

    [Fact]
    public void InvalidSettings_AreRefused_AndTheFileIsLeftAsItWas()
    {
        var store = Store();
        store.Save(Settings());
        var before = File.ReadAllText(File_);
        var broken = ChannelStore.Masked(store.Current);
        broken.Email.From = "not an address";

        var ex = Assert.Throws<ArgumentException>(() => store.Save(broken));

        Assert.Contains("mittente", ex.Message);
        Assert.Equal(before, File.ReadAllText(File_));
        Assert.Equal("helpdesk@example.com", store.Current.Email.From);
    }

    [Fact]
    public void ADamagedFile_SwitchesTheOutsideChannelsOff_WithoutBeingOverwritten()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(File_, "{ this is not json");

        var current = Store().Current;

        Assert.False(current.Email.Enabled);
        Assert.Equal("{ this is not json", File.ReadAllText(File_));
    }

    [Fact]
    public void TheVersion_ChangesWhenTheSettingsDo()
    {
        var store = Store();
        var before = store.Version;

        store.Save(Settings());

        Assert.NotEqual(before, store.Version);
    }

    [Fact]
    public async Task TheRegistry_FollowsTheSettings()
    {
        var store = Store();
        var registry = new ChannelRegistry(store, new HttpClient(), new RecordingEmailSender());
        Assert.Null(registry.Get("email"));

        store.Save(Settings());

        var email = registry.Get("email");
        Assert.NotNull(email);
        Assert.NotNull(registry.Get("teams"));
        Assert.Null(registry.Get("whatsapp"));
        Assert.Same(email, registry.Get("email")); // not rebuilt for nothing

        var off = ChannelStore.Masked(store.Current);
        off.Email.Enabled = false;
        store.Save(off);
        Assert.Null(registry.Get("email"));
        await Task.CompletedTask;
    }
}
