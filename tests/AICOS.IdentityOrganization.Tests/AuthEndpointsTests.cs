using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.IdentityOrganization.Tests;

public sealed class AuthEndpointsTests : IDisposable
{
    internal const string Auth = "/api/v1/identity/auth";
    internal const string Password = "Secret123";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new TimeOnlyJsonConverter() },
    };

    private readonly IdentityApiFactory _factory = new();
    private readonly HttpClient _anonymous;

    public AuthEndpointsTests() => _anonymous = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    // ------------------------------------------------------------------ register (email, active at once)
    [Fact]
    public async Task Register_creates_an_active_platform_wide_customer_and_signs_in_without_a_code()
    {
        _factory.Emails.Fail = new IOException("the mail server is down"); // no mail is sent, so a mail outage does not matter

        var response = await StartSignUpAsync("an@example.com");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        auth.AccessToken.Should().NotBeNullOrEmpty();
        auth.User.Role.Should().Be(AicosRoles.Customer);
        auth.User.Email.Should().Be("an@example.com");
        auth.User.FullName.Should().Be("Test User");
        auth.User.TenantId.Should().BeNull();
        auth.ActiveBrandCode.Should().BeNull();
        _factory.Emails.Messages.Should().BeEmpty();
        _factory.Events.Published.OfType<CustomerRegisteredEvent>().Should().BeEmpty(); // the profile is created at the first brand chosen

        (await LoginAsync("an@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Registering_over_a_pending_account_left_from_the_code_sign_up_replaces_it()
    {
        await SeedPendingAsync("giang2@example.com");

        var again = await _anonymous.PostAsJsonAsync($"{Auth}/register",
            new RegisterRequest("Giang Mới", "giang2@example.com", "NewPass123", "NewPass123"), Json);

        again.StatusCode.Should().Be(HttpStatusCode.Created);
        (await again.Content.ReadFromJsonAsync<AuthResponse>(Json))!.User.FullName.Should().Be("Giang Mới");
        (await LoginAsync("giang2@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync("giang2@example.com", "NewPass123")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_normalizes_email_and_rejects_a_duplicate_case_insensitively()
    {
        await SignUpAsync(_anonymous, _factory, "  Binh@Example.com ");

        var duplicate = await StartSignUpAsync("binh@example.com");

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(duplicate)).Should().Be("user.duplicate_email");
    }

    // ------------------------------------------------------------------ pending accounts from the code sign-up: verify and resend still work
    [Fact]
    public async Task A_pending_account_is_still_activated_by_its_code()
    {
        await SeedPendingAsync("binh2@example.com");
        await ResendAsync("binh2@example.com");

        var login = await LoginAsync("binh2@example.com", Password);
        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(login)).Should().Be("auth.email_not_verified");

        var verify = await VerifyAsync("binh2@example.com", _factory.Emails.LastCodeFor("binh2@example.com"));
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        (await verify.Content.ReadFromJsonAsync<AuthResponse>(Json))!.User.Role.Should().Be(AicosRoles.Customer);
        (await LoginAsync("binh2@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await VerifyAsync("binh2@example.com", _factory.Emails.LastCodeFor("binh2@example.com")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity); // the code is single use
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_the_code_dies_after_five_attempts()
    {
        await SeedPendingAsync("chi2@example.com");
        await ResendAsync("chi2@example.com");
        var code = _factory.Emails.LastCodeFor("chi2@example.com");
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var refused = await VerifyAsync("chi2@example.com", wrong);
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await ErrorCodeAsync(refused)).Should().Be("auth.verification_code_invalid");
        }

        (await VerifyAsync("chi2@example.com", code)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        await SeedPendingAsync("dao@example.com");
        await ResendAsync("dao@example.com");
        var code = _factory.Emails.LastCodeFor("dao@example.com");

        _factory.Clock.Advance(TimeSpan.FromMinutes(11));

        (await VerifyAsync("dao@example.com", code)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Resend_waits_a_minute_and_then_the_new_code_replaces_the_old_one()
    {
        await SeedPendingAsync("em2@example.com");
        await ResendAsync("em2@example.com");
        var first = _factory.Emails.LastCodeFor("em2@example.com");

        var tooSoon = await ResendAsync("em2@example.com");
        tooSoon.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await tooSoon.Content.ReadFromJsonAsync<SignUpPendingResponse>(Json))!.ResendAfterSeconds.Should().BeInRange(1, 60);
        _factory.Emails.Messages.Should().HaveCount(1);

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        (await ResendAsync("em2@example.com")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Emails.Messages.Should().HaveCount(2);

        var second = _factory.Emails.LastCodeFor("em2@example.com");
        if (second != first)
        {
            (await VerifyAsync("em2@example.com", first)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }

        (await VerifyAsync("em2@example.com", second)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_validates_all_fields()
    {
        var response = await _anonymous.PostAsJsonAsync($"{Auth}/register",
            new RegisterRequest("", "not-an-email", "short", "different"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var field in new[] { "FullName", "Email", "Password", "ConfirmPassword" })
        {
            errors.TryGetProperty(field, out _).Should().BeTrue($"{field} should be reported");
        }
    }

    [Fact]
    public async Task Register_ignores_any_attempt_to_choose_a_role()
    {
        var response = await _anonymous.PostAsJsonAsync($"{Auth}/register", new
        {
            fullName = "Hacker", email = "h@example.com", password = Password, confirmPassword = Password, role = "BrandAdmin",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!.User.Role.Should().Be(AicosRoles.Customer);
    }

    // ------------------------------------------------------------------ Google
    [Fact]
    public async Task Google_creates_an_active_platform_wide_customer_without_a_code_and_signs_in_again_later()
    {
        _factory.Google.Accept("good-token", new GoogleIdentity("g-123", "Lan.Google@Example.com", true, "Lan Google"));

        var first = await GoogleAsync("good-token");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var auth = (await first.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        (auth.User.Email, auth.User.FullName, auth.User.Role).Should().Be(("lan.google@example.com", "Lan Google", AicosRoles.Customer));
        auth.User.TenantId.Should().BeNull();
        _factory.Emails.Messages.Should().BeEmpty();

        var second = (await (await GoogleAsync("good-token")).Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        second.User.Id.Should().Be(auth.User.Id);
    }

    [Fact]
    public async Task Google_with_the_email_of_a_pending_sign_up_completes_it()
    {
        await SeedPendingAsync("mai@example.com");
        _factory.Google.Accept("mai-token", new GoogleIdentity("g-mai", "mai@example.com", true, "Mai"));

        (await GoogleAsync("mai-token")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await LoginAsync("mai@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Google_refuses_a_bad_token_an_unverified_email_and_a_staff_account()
    {
        var bad = await GoogleAsync("unknown-token");
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(bad)).Should().Be("auth.google_invalid_token");

        _factory.Google.Accept("unverified", new GoogleIdentity("g-u", "u@example.com", false, "U"));
        (await ErrorCodeAsync(await GoogleAsync("unverified"))).Should().Be("auth.google_email_unverified");

        var branch = await CreateBranchAsync();
        await CreateUserAsync("staff2@example.com", AicosRoles.Cashier, branch.Id);
        _factory.Google.Accept("staff", new GoogleIdentity("g-s", "staff2@example.com", true, "Staff"));
        var staff = await GoogleAsync("staff");
        staff.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(staff)).Should().Be("auth.google_not_allowed");
    }

    // ------------------------------------------------------------------ choosing a brand
    [Fact]
    public async Task Choosing_a_brand_puts_it_in_the_token_creates_the_profile_and_survives_a_refresh()
    {
        var signedIn = await SignUpAsync(_anonymous, _factory, "kim@example.com");
        TenantClaim(signedIn.AccessToken).Should().BeNull();
        using var client = ClientWith(signedIn.AccessToken);

        var chosen = await SelectBrandAsync(client, signedIn.RefreshToken, IdentityApiFactory.TenantCode);

        chosen.ActiveBrandCode.Should().Be(IdentityApiFactory.TenantCode);
        TenantClaim(chosen.AccessToken).Should().Be(IdentityApiFactory.TenantId.ToString());
        _factory.Events.Published.OfType<CustomerRegisteredEvent>()
            .Should().ContainSingle(e => e.UserId == signedIn.User.Id && e.TenantId == IdentityApiFactory.TenantId && e.Email == "kim@example.com");

        var refreshed = await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(chosen.RefreshToken), Json);
        var afterRefresh = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        afterRefresh.ActiveBrandCode.Should().Be(IdentityApiFactory.TenantCode);
        TenantClaim(afterRefresh.AccessToken).Should().Be(IdentityApiFactory.TenantId.ToString());
    }

    [Fact]
    public async Task One_account_can_move_between_brands_also_by_id_from_a_table_qr()
    {
        using var superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);
        var second = await superAdmin.PostAsJsonAsync("/api/v1/identity/tenants", new { name = "Second Brand", code = "second-brand", firstAdmin = new { fullName = "Owner", email = "owner2@example.com", password = Password } }, Json);
        second.EnsureSuccessStatusCode();
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var signedIn = await SignUpAsync(_anonymous, _factory, "lien@example.com");
        using var client = ClientWith(signedIn.AccessToken);
        var first = await SelectBrandAsync(client, signedIn.RefreshToken, IdentityApiFactory.TenantCode);
        var moved = await client.PostAsJsonAsync($"{Auth}/select-brand", new SelectBrandRequest(first.RefreshToken, null, secondId), Json);

        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        var auth = (await moved.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        auth.ActiveBrandCode.Should().Be("second-brand");
        TenantClaim(auth.AccessToken).Should().Be(secondId.ToString());
        _factory.Events.Published.OfType<CustomerRegisteredEvent>().Where(e => e.UserId == signedIn.User.Id).Select(e => e.TenantId)
            .Should().BeEquivalentTo(new[] { IdentityApiFactory.TenantId, secondId });
    }

    [Fact]
    public async Task Choosing_a_brand_is_refused_for_unknown_or_suspended_brands_and_for_staff()
    {
        var signedIn = await SignUpAsync(_anonymous, _factory, "mo@example.com");
        using var client = ClientWith(signedIn.AccessToken);
        (await client.PostAsJsonAsync($"{Auth}/select-brand", new SelectBrandRequest(signedIn.RefreshToken, "nope", null), Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"{Auth}/select-brand", new SelectBrandRequest("wrong-token", IdentityApiFactory.TenantCode, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.PostAsJsonAsync($"{Auth}/select-brand", new SelectBrandRequest(signedIn.RefreshToken, IdentityApiFactory.TenantCode, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);
        await superAdmin.PatchAsJsonAsync($"/api/v1/identity/tenants/{IdentityApiFactory.TenantId}/status", new { status = "Suspended" }, Json);
        var suspended = await client.PostAsJsonAsync($"{Auth}/select-brand", new SelectBrandRequest(signedIn.RefreshToken, IdentityApiFactory.TenantCode, null), Json);
        suspended.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var staff = _factory.CreateClientAs(AicosRoles.Cashier);
        (await staff.PostAsJsonAsync($"{Auth}/select-brand", new SelectBrandRequest(signedIn.RefreshToken, IdentityApiFactory.TenantCode, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_brand_picker_lists_only_active_brands_that_have_an_active_branch()
    {
        (await _anonymous.GetFromJsonAsync<JsonElement>("/api/v1/identity/public/brands")).GetArrayLength().Should().Be(0); // no branch yet

        await CreateBranchAsync();

        var brands = await _anonymous.GetFromJsonAsync<JsonElement>("/api/v1/identity/public/brands");
        brands.GetArrayLength().Should().Be(1);
        brands[0].GetProperty("code").GetString().Should().Be(IdentityApiFactory.TenantCode);
        brands[0].GetProperty("branchCount").GetInt32().Should().Be(1);
    }

    // ------------------------------------------------------------------ UC-66 delete my data
    [Fact]
    public async Task Deleting_my_data_needs_the_password_anonymizes_the_account_and_ends_every_session()
    {
        var signedIn = await SignUpAsync(_anonymous, _factory, "xoa@example.com");
        using var client = ClientWith(signedIn.AccessToken);

        (await client.PostAsJsonAsync($"{Auth}/me/delete", new DeleteMyDataRequest("Wrong1234"), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync($"{Auth}/me/delete", new DeleteMyDataRequest(Password), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        _factory.Events.Published.OfType<CustomerDataErasedEvent>().Should().ContainSingle(e => e.UserId == signedIn.User.Id);
        (await LoginAsync("xoa@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(signedIn.RefreshToken))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StartSignUpAsync("xoa@example.com")).StatusCode.Should().Be(HttpStatusCode.Created); // the address is free again
    }

    [Fact]
    public async Task Staff_cannot_use_the_customer_data_deletion()
    {
        using var staff = _factory.CreateClientAs(AicosRoles.Cashier);
        (await staff.PostAsJsonAsync($"{Auth}/me/delete", new DeleteMyDataRequest(Password), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ login
    [Fact]
    public async Task Login_with_email_or_username_returns_tokens_usable_on_me()
    {
        await RegisterAsync("chi@example.com");

        var auth = await LoginOkAsync("CHI@example.com");
        auth.TokenType.Should().Be("Bearer");
        auth.RefreshToken.Should().NotBeNullOrWhiteSpace();
        auth.User.LastLoginAtUtc.Should().NotBeNull();

        using var client = ClientWith(auth.AccessToken);
        var me = await client.GetFromJsonAsync<UserResponse>($"{Auth}/me", Json);
        me!.Email.Should().Be("chi@example.com");
    }

    [Fact]
    public async Task Login_by_username_works_for_admin_created_staff()
    {
        var branch = await CreateBranchAsync();
        await CreateUserAsync("cashier1@example.com", AicosRoles.Cashier, branch.Id, username: "Cashier01");

        var auth = await LoginOkAsync("cashier01");

        auth.User.Role.Should().Be(AicosRoles.Cashier);
        auth.User.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_password_returns_401_with_remaining_attempts()
    {
        await RegisterAsync("dung@example.com");

        var response = await LoginAsync("dung@example.com", "Wrong1234");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be("auth.invalid_credentials");
        problem.GetProperty("detail").GetString().Should().Contain("4 attempt(s) left");
    }

    [Fact]
    public async Task Unknown_account_returns_401()
    {
        var response = await LoginAsync("nobody@example.com", Password);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account_even_for_the_right_password()
    {
        await RegisterAsync("em@example.com");
        for (var i = 1; i <= 4; i++)
        {
            (await LoginAsync("em@example.com", "Wrong1234")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var fifth = await LoginAsync("em@example.com", "Wrong1234");
        fifth.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(fifth)).Should().Be("account.temporarily_locked");

        var correct = await LoginAsync("em@example.com", Password);
        correct.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(correct)).Should().Be("account.temporarily_locked");
    }

    [Fact]
    public async Task A_locked_account_tells_the_client_when_to_try_again()
    {
        await RegisterAsync("hoa@example.com");
        for (var i = 1; i <= 4; i++)
        {
            await LoginAsync("hoa@example.com", "Wrong1234");
        }

        var fifth = await LoginAsync("hoa@example.com", "Wrong1234");
        var again = await LoginAsync("hoa@example.com", Password);

        foreach (var response in new[] { fifth, again })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("errorCode").GetString().Should().Be("account.temporarily_locked");
            var seconds = problem.GetProperty("retryAfterSeconds").GetInt32();
            seconds.Should().BeInRange(1, 15 * 60);
            problem.GetProperty("lockedUntilUtc").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
            response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().Be(seconds);
        }
    }

    [Fact]
    public async Task Staff_of_a_suspended_branch_cannot_log_in()
    {
        var branch = await CreateBranchAsync();
        await CreateUserAsync("staff@example.com", AicosRoles.BranchManager, branch.Id);
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin);
        await admin.PatchAsJsonAsync($"/api/v1/identity/branches/{branch.Id}/status", new { status = "Suspended" });

        var response = await LoginAsync("staff@example.com", Password);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("branch.suspended");
    }

    // ------------------------------------------------------------------ refresh / logout
    [Fact]
    public async Task Refresh_rotates_the_token_and_the_old_one_stops_working()
    {
        await RegisterAsync("giang@example.com");
        var first = await LoginOkAsync("giang@example.com");

        var refreshed = await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(first.RefreshToken));
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        second.RefreshToken.Should().NotBe(first.RefreshToken);

        var reuse = await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(first.RefreshToken));
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(reuse)).Should().Be("auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token_and_is_idempotent()
    {
        await RegisterAsync("ha@example.com");
        var auth = await LoginOkAsync("ha@example.com");

        (await _anonymous.PostAsJsonAsync($"{Auth}/logout", new RefreshTokenRequest(auth.RefreshToken)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _anonymous.PostAsJsonAsync($"{Auth}/logout", new RefreshTokenRequest(auth.RefreshToken)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(auth.RefreshToken)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_requires_a_token()
    {
        (await _anonymous.GetAsync($"{Auth}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ change password
    [Fact]
    public async Task Change_password_requires_the_current_one_and_ends_all_sessions()
    {
        await RegisterAsync("khanh@example.com");
        var auth = await LoginOkAsync("khanh@example.com");
        using var client = ClientWith(auth.AccessToken);

        var wrong = await client.PostAsJsonAsync($"{Auth}/change-password", new ChangePasswordRequest("Nope1234", "NewPass123", "NewPass123"));
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await client.PostAsJsonAsync($"{Auth}/change-password", new ChangePasswordRequest(Password, "NewPass123", "NewPass123"));
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(auth.RefreshToken)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync("khanh@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync("khanh@example.com", "NewPass123")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ forgot / reset password
    [Fact]
    public async Task Forgot_and_reset_password_flow()
    {
        await RegisterAsync("lan@example.com");

        var forgot = await _anonymous.PostAsJsonAsync($"{Auth}/forgot-password", new ForgotPasswordRequest("lan@example.com"));
        forgot.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var code = _factory.Emails.LastCodeFor("lan@example.com");

        var reset = await _anonymous.PostAsJsonAsync($"{Auth}/reset-password",
            new ResetPasswordRequest("lan@example.com", code, "Brand1234", "Brand1234"));
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await LoginAsync("lan@example.com", "Brand1234")).StatusCode.Should().Be(HttpStatusCode.OK);

        var reuse = await _anonymous.PostAsJsonAsync($"{Auth}/reset-password",
            new ResetPasswordRequest("lan@example.com", code, "Again1234", "Again1234"));
        reuse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Forgot_password_for_unknown_email_looks_identical_and_sends_nothing()
    {
        var response = await _anonymous.PostAsJsonAsync($"{Auth}/forgot-password", new ForgotPasswordRequest("ghost@example.com"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Wrong_reset_code_is_rejected_and_code_dies_after_max_attempts()
    {
        await RegisterAsync("minh@example.com");
        await _anonymous.PostAsJsonAsync($"{Auth}/forgot-password", new ForgotPasswordRequest("minh@example.com"));
        var code = _factory.Emails.LastCodeFor("minh@example.com");
        var wrongCode = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            (await _anonymous.PostAsJsonAsync($"{Auth}/reset-password",
                new ResetPasswordRequest("minh@example.com", wrongCode, "Brand1234", "Brand1234")))
                .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }

        // Even the correct code no longer works after 5 wrong attempts.
        (await _anonymous.PostAsJsonAsync($"{Auth}/reset-password",
            new ResetPasswordRequest("minh@example.com", code, "Brand1234", "Brand1234")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_reset_code_is_checked_first_without_being_used_up_and_wrong_tries_count()
    {
        await RegisterAsync("quang@example.com");
        await _anonymous.PostAsJsonAsync($"{Auth}/forgot-password", new ForgotPasswordRequest("quang@example.com"));
        var code = _factory.Emails.LastCodeFor("quang@example.com");
        var wrongCode = code == "000000" ? "111111" : "000000";

        var wrong = await _anonymous.PostAsJsonAsync($"{Auth}/reset-password/verify", new VerifyResetCodeRequest("quang@example.com", wrongCode));
        wrong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(wrong)).Should().Be("auth.reset_code_invalid");
        (await _anonymous.PostAsJsonAsync($"{Auth}/reset-password/verify", new VerifyResetCodeRequest("quang@example.com", "12")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _anonymous.PostAsJsonAsync($"{Auth}/reset-password/verify", new VerifyResetCodeRequest("quang@example.com", code)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _anonymous.PostAsJsonAsync($"{Auth}/reset-password",
            new ResetPasswordRequest("quang@example.com", code, "Brand1234", "Brand1234"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _anonymous.PostAsJsonAsync($"{Auth}/reset-password/verify", new VerifyResetCodeRequest("quang@example.com", code)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity); // used by the reset

        // Wrong tries on the check share the reset's limit of five.
        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await _anonymous.PostAsJsonAsync($"{Auth}/forgot-password", new ForgotPasswordRequest("quang@example.com"));
        var second = _factory.Emails.LastCodeFor("quang@example.com");
        var miss = second == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++)
        {
            await _anonymous.PostAsJsonAsync($"{Auth}/reset-password/verify", new VerifyResetCodeRequest("quang@example.com", miss));
        }

        (await _anonymous.PostAsJsonAsync($"{Auth}/reset-password/verify", new VerifyResetCodeRequest("quang@example.com", second)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Reset_password_unlocks_a_temporarily_locked_account()
    {
        await RegisterAsync("nga@example.com");
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync("nga@example.com", "Wrong1234");
        }

        await _anonymous.PostAsJsonAsync($"{Auth}/forgot-password", new ForgotPasswordRequest("nga@example.com"));
        await _anonymous.PostAsJsonAsync($"{Auth}/reset-password",
            new ResetPasswordRequest("nga@example.com", _factory.Emails.LastCodeFor("nga@example.com"), "Brand1234", "Brand1234"));

        (await LoginAsync("nga@example.com", "Brand1234")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ helpers
    internal static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private Task<HttpResponseMessage> StartSignUpAsync(string email) =>
        _anonymous.PostAsJsonAsync($"{Auth}/register", new RegisterRequest("Test User", email, Password, Password), Json);

    private Task<HttpResponseMessage> VerifyAsync(string email, string code) =>
        _anonymous.PostAsJsonAsync($"{Auth}/register/verify", new VerifyRegistrationRequest(email, code), Json);

    private Task<HttpResponseMessage> ResendAsync(string email) =>
        _anonymous.PostAsJsonAsync($"{Auth}/register/resend", new ResendRegistrationRequest(email), Json);

    /// <summary>A customer account still waiting for its email code, as the sign-up created them before registering became one step.</summary>
    private async Task SeedPendingAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var user = new User("Test User", email, username: null, phone: null, AicosRoles.Customer, branchId: null);
        user.SetPassword(passwords.Hash(user, Password), mustChangePassword: false);
        user.ChangeStatus(UserStatus.PendingVerification);
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> GoogleAsync(string idToken) =>
        _anonymous.PostAsJsonAsync($"{Auth}/google", new GoogleLoginRequest(idToken), Json);

    private static async Task<AuthResponse> SelectBrandAsync(HttpClient client, string refreshToken, string code)
    {
        var response = await client.PostAsJsonAsync($"{Auth}/select-brand", new SelectBrandRequest(refreshToken, code, null), Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
    }

    private static string? TenantClaim(string accessToken) =>
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.FirstOrDefault(c => c.Type == AicosClaimTypes.TenantId)?.Value;

    /// <summary>A customer who signed up: registering creates the account active and signs in.</summary>
    private async Task<HttpResponseMessage> RegisterAsync(string email)
    {
        var response = await StartSignUpAsync(email);
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>Sign-up for tests of other features; returns the signed-in session.</summary>
    internal static async Task<AuthResponse> SignUpAsync(
        HttpClient client, IdentityApiFactory factory, string email, string password = Password, string fullName = "Test User")
    {
        var address = email.Trim().ToLowerInvariant();
        var registered = await client.PostAsJsonAsync($"{Auth}/register", new RegisterRequest(fullName, address, password, password), Json);
        registered.EnsureSuccessStatusCode();
        return (await registered.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
    }

    private Task<HttpResponseMessage> LoginAsync(string login, string password) =>
        _anonymous.PostAsJsonAsync($"{Auth}/login", new LoginRequest(login, password), Json);

    [Fact]
    public async Task A_person_lists_their_devices_and_signs_the_others_out()
    {
        await RegisterAsync("dan@example.com");
        var here = await LoginOkAsync("dan@example.com");
        var phone = await LoginOkAsync("dan@example.com");
        var tablet = await LoginOkAsync("dan@example.com");
        await RegisterAsync("em@example.com");
        var stranger = await LoginOkAsync("em@example.com");
        using var client = ClientWith(here.AccessToken);

        var sessions = (await client.GetFromJsonAsync<List<SessionResponse>>($"{Auth}/sessions", Json))!;
        sessions.Where(s => s.IsCurrent).Should().ContainSingle();
        var others = sessions.Where(s => !s.IsCurrent).ToList();

        using var strangerClient = ClientWith(stranger.AccessToken);
        (await strangerClient.DeleteAsync($"{Auth}/sessions/{others[0].Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);   // only your own
        (await client.DeleteAsync($"{Auth}/sessions/{others[0].Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<SessionResponse>>($"{Auth}/sessions", Json))!.Should().HaveCount(sessions.Count - 1);

        var revoked = await client.PostAsJsonAsync($"{Auth}/sessions/revoke-others", new { }, Json);
        (await revoked.Content.ReadFromJsonAsync<RevokedSessionsResponse>(Json))!.Revoked.Should().Be(sessions.Count - 2);
        var left = (await client.GetFromJsonAsync<List<SessionResponse>>($"{Auth}/sessions", Json))!;
        left.Should().ContainSingle().Which.IsCurrent.Should().BeTrue();

        (await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(phone.RefreshToken), Json)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(tablet.RefreshToken), Json)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(here.RefreshToken), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<AuthResponse> LoginOkAsync(string login)
    {
        var response = await LoginAsync(login, Password);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
    }

    private HttpClient ClientWith(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<BranchResponse> CreateBranchAsync()
    {
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin);
        var response = await admin.PostAsJsonAsync("/api/v1/identity/branches", new BranchRequest($"Branch {Guid.NewGuid():N}", null, null, null, null), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private async Task CreateUserAsync(string email, string role, Guid? branchId, string? username = null)
    {
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin);
        var response = await admin.PostAsJsonAsync("/api/v1/identity/users",
            new CreateUserRequest("Staff Member", email, username, null, Password, role, branchId), Json);
        response.EnsureSuccessStatusCode();
    }
}
