using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>Size, sugar, ice and topping options on an order line (BR-13, BR-14): validated against the menu, priced into the unit price, shown to the bar.</summary>
public sealed class OrderOptionTests : IDisposable
{
    private const string Orders = "/api/v1/orders";

    private readonly FakeMenu _menu = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _b1;
    private readonly Guid _latte;
    private readonly Guid _tea;
    private readonly HttpClient _cashier;
    private readonly HttpClient _customer;

    private readonly Guid _sizeGroup = Guid.NewGuid();
    private readonly Guid _sizeM = Guid.NewGuid();
    private readonly Guid _sizeL = Guid.NewGuid();
    private readonly Guid _sizeXl = Guid.NewGuid();
    private readonly Guid _hot = Guid.NewGuid();
    private readonly Guid _cold = Guid.NewGuid();
    private readonly Guid _pearl = Guid.NewGuid();
    private readonly Guid _foam = Guid.NewGuid();
    private readonly Guid _jelly = Guid.NewGuid();

    public OrderOptionTests()
    {
        _f = new ServiceFactory<OrderingService, OrderingDbContext>(services =>
        {
            services.RemoveAll<IMenuCatalogClient>();
            services.AddSingleton<IMenuCatalogClient>(_menu);
        });
        _f.Clock.Now = new DateTimeOffset(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _latte = _menu.Add(_b1, "Latte", 55_000);
        _tea = _menu.Add(_b1, "Trà", 30_000);
        _menu.SetOptions(_b1, _latte,
            new MenuOptionGroupInfo(_sizeGroup, "Size", false, true, null,
                [new(_sizeM, "M", 0, true, null), new(_sizeL, "L", 5_000, false, null), new(_sizeXl, "XL", 10_000, false, null)]),
            new MenuOptionGroupInfo(Guid.NewGuid(), "Nhiệt độ", false, false, null, [new(_hot, "Nóng", 0, false, null), new(_cold, "Lạnh", 0, false, null)]),
            new MenuOptionGroupInfo(Guid.NewGuid(), "Topping", true, false, 2,
                [new(_pearl, "Trân châu", 7_000, false, _cold), new(_foam, "Kem cheese", 5_000, false, null), new(_jelly, "Thạch", 6_000, false, null)]));
        _menu.SetOptions(_b1, _tea, new MenuOptionGroupInfo(Guid.NewGuid(), "Đường", false, true, null, [new(Guid.NewGuid(), "100%", 0, false, null), new(Guid.NewGuid(), "50%", 0, false, null)]));
        _cashier = _f.As(AicosRoles.Cashier, _b1);
        _customer = _f.As(AicosRoles.Customer, userId: Guid.NewGuid());
    }

    public void Dispose() => _f.Dispose();

    private static object[] Items(params object[] items) => items;

    private Task<HttpResponseMessage> OrderAsync(params object[] items) => _cashier.Post(Orders, new { type = "Takeaway", items });

    private async Task<JsonElement> OrderOkAsync(params object[] items) => await (await OrderAsync(items)).Body();

    [Fact]
    public async Task Size_and_topping_surcharges_are_added_to_the_unit_price()
    {
        var order = await OrderOkAsync(new { productId = _latte, quantity = 2, optionIds = new[] { _sizeL, _foam, _cold }, note = "ít đá" });

        var line = order.GetProperty("items").EnumerateArray().Single();
        line.GetProperty("unitPrice").GetDecimal().Should().Be(65_000);                      // 55,000 + L 5,000 + cheese foam 5,000
        line.GetProperty("basePrice").GetDecimal().Should().Be(55_000);
        line.GetProperty("totalPrice").GetDecimal().Should().Be(130_000);
        line.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("name").GetString()).Should().BeEquivalentTo("L", "Kem cheese", "Lạnh");
        line.GetProperty("options").EnumerateArray().First(o => o.GetProperty("name").GetString() == "L").GetProperty("groupName").GetString().Should().Be("Size");
        order.GetProperty("finalAmount").GetDecimal().Should().Be(130_000);
    }

    [Fact]
    public async Task A_required_group_the_customer_did_not_answer_takes_its_default()
    {
        var order = await OrderOkAsync(new { productId = _latte, quantity = 1 });

        var line = order.GetProperty("items").EnumerateArray().Single();
        line.GetProperty("unitPrice").GetDecimal().Should().Be(55_000);
        line.GetProperty("options").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("M");
    }

    [Fact]
    public async Task A_price_in_the_request_cannot_change_what_the_options_cost()
    {
        var response = await OrderAsync(new { productId = _latte, quantity = 1, optionIds = new[] { _sizeXl }, unitPrice = 1, optionPrice = 0 });

        (await response.Body()).GetProperty("finalAmount").GetDecimal().Should().Be(65_000);
    }

    [Fact]
    public async Task Options_that_break_the_menu_rules_are_refused()
    {
        await OrderAsync(new { productId = _latte, quantity = 1, optionIds = new[] { Guid.NewGuid() } }).ShouldBe(422, "order.option_invalid");
        await OrderAsync(new { productId = _tea, quantity = 1, optionIds = new[] { _sizeL } }).ShouldBe(422, "order.option_invalid");          // an option of another product
        await OrderAsync(new { productId = _latte, quantity = 1, optionIds = new[] { _sizeM, _sizeL } }).ShouldBe(422, "order.option_too_many");   // single choice
        await OrderAsync(new { productId = _latte, quantity = 1, optionIds = new[] { _foam, _jelly, _pearl, _cold } }).ShouldBe(422, "order.option_too_many");   // at most 2 toppings
        await OrderAsync(new { productId = _latte, quantity = 1, optionIds = new[] { _pearl } }).ShouldBe(422, "order.option_requires");        // pearl needs cold
        await OrderAsync(new { productId = _tea, quantity = 1 }).ShouldBe(422, "order.option_required");                                       // a required group without a default
        await OrderAsync(new { productId = _latte, quantity = 1, optionIds = new[] { _pearl, _cold } }).ShouldBe(201);
    }

    [Fact]
    public async Task The_same_option_twice_counts_once()
    {
        var order = await OrderOkAsync(new { productId = _latte, quantity = 1, optionIds = new[] { _sizeL, _sizeL } });

        order.GetProperty("finalAmount").GetDecimal().Should().Be(60_000);
    }

    [Fact]
    public async Task Lines_with_the_same_options_are_merged_and_different_options_stay_apart()
    {
        var order = await OrderOkAsync(
            new { productId = _latte, quantity = 1, optionIds = new[] { _sizeL } },
            new { productId = _latte, quantity = 2, optionIds = new[] { _sizeL } },
            new { productId = _latte, quantity = 1, optionIds = new[] { _sizeXl } });

        var lines = order.GetProperty("items").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines.Single(l => l.GetProperty("unitPrice").GetDecimal() == 60_000).GetProperty("quantity").GetInt32().Should().Be(3);
        order.GetProperty("finalAmount").GetDecimal().Should().Be(3 * 60_000 + 65_000);
    }

    [Fact]
    public async Task The_bar_reads_the_options_in_the_item_note_and_the_options_stay_on_the_order_later()
    {
        var order = await OrderOkAsync(new { productId = _latte, quantity = 1, optionIds = new[] { _sizeL, _foam }, note = "ít đá" });

        var created = _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Single();
        var note = created.Items.Single().Note!;
        note.Should().Contain("L").And.Contain("Kem cheese").And.EndWith("ít đá");
        created.Items.Single().UnitPrice.Should().Be(65_000);                               // reports count the price with options

        var read = await _cashier.GetFromJsonAsync<JsonElement>($"{Orders}/{order.Id()}");
        read.GetProperty("items")[0].GetProperty("options").GetArrayLength().Should().Be(2);
        var receipt = await _cashier.GetFromJsonAsync<JsonElement>($"{Orders}/{order.Id()}/receipt");
        receipt.GetProperty("items")[0].GetProperty("options").EnumerateArray().Select(o => o.GetString()).Should().BeEquivalentTo("L", "Kem cheese");
    }

    [Fact]
    public async Task A_pickup_order_prices_the_options_too()
    {
        var slot = new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc);                 // 11:00 local

        var response = await _customer.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = slot, items = new object[] { new { productId = _latte, quantity = 2, optionIds = new[] { _sizeXl, _jelly } } } });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var order = await response.Body();
        order.GetProperty("finalAmount").GetDecimal().Should().Be(2 * (55_000 + 10_000 + 6_000));
        await _customer.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = slot, items = new object[] { new { productId = _latte, quantity = 1, optionIds = new[] { _pearl } } } })
            .ShouldBe(422, "order.option_requires");
    }
}
