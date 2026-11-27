using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data;
using SmartHotel.Data.Seed;

namespace SmartHotel.Api.IntegrationTests.Data;

// The factory starts the API host during fixture setup, and the host migrates and seeds this class's database before
// it starts, so every test here reads the catalogue the startup seed inserted. No test writes a row.
public sealed class SeedTests : IClassFixture<SmartHotelApiFactory>
{
    private const int RoomsPerType = 10;
    private const int FloorNumberMultiplier = 100;
    private const decimal HotelPriceStep = 10.00m;

    // The seed-data tables written out as literals rather than read from SeedCatalog: expectations built from the
    // seeder's own data would pass with a wrong catalogue.
    private static readonly ExpectedHotel[] ExpectedHotels =
    [
        new(1, "SmartHotel Seattle Downtown", "Seattle"),
        new(2, "SmartHotel Seattle Waterfront", "Seattle"),
        new(3, "SmartHotel New York Midtown", "New York"),
        new(4, "SmartHotel New York Brooklyn", "New York"),
        new(5, "SmartHotel Chicago Loop", "Chicago"),
    ];

    private static readonly ExpectedRoomType[] ExpectedRoomTypes =
    [
        new(1, "Standard", 2, 129.00m),
        new(2, "Deluxe", 2, 169.00m),
        new(3, "Family", 4, 219.00m),
        new(4, "Suite", 4, 329.00m),
    ];

    private static readonly (string City, int HotelCount)[] ExpectedHotelsPerCity =
    [
        ("Chicago", 1),
        ("New York", 2),
        ("Seattle", 2),
    ];

    private readonly SmartHotelApiFactory _factory;

    public SeedTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Seed_Catalogue_HasFiveHotelsInThreeCitiesWithTwentyRoomTypesAndTwoHundredRooms()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot catalogue = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);
        (bool anyGuest, bool anyBooking) = await _factory.ExecuteDbAsync(
            static async (db, token) => (
                await db.Guests.AsNoTracking().AnyAsync(token),
                await db.Bookings.AsNoTracking().AnyAsync(token)),
            ct);

        Assert.Equal(5, catalogue.Hotels.Count);
        Assert.Equal(
            ExpectedHotels
                .Select(static hotel => (hotel.Name, hotel.City))
                .OrderBy(static hotel => hotel.Name, StringComparer.Ordinal)
                .ToArray(),
            catalogue.Hotels
                .Select(static hotel => (hotel.Name, hotel.City))
                .OrderBy(static hotel => hotel.Name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ExpectedHotelsPerCity,
            catalogue.Hotels
                .GroupBy(static hotel => hotel.City, StringComparer.Ordinal)
                .Select(static city => (City: city.Key, HotelCount: city.Count()))
                .OrderBy(static city => city.City, StringComparer.Ordinal)
                .ToArray());
        Assert.All(catalogue.Hotels, static hotel =>
        {
            Assert.False(string.IsNullOrWhiteSpace(hotel.Address));
            Assert.False(string.IsNullOrWhiteSpace(hotel.Description));
        });

        Assert.Equal(20, catalogue.RoomTypes.Count);
        Assert.Equal(200, catalogue.Rooms.Count);

        // The seed inserts the catalogue only: no personal data and no bookings.
        Assert.False(anyGuest);
        Assert.False(anyBooking);
    }

    [Fact]
    public async Task Seed_RoomTypes_HaveExpectedNamesCapacitiesAndPrices()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot catalogue = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);

        string[] expectedNames =
            [.. ExpectedRoomTypes.Select(static type => type.Name).Order(StringComparer.Ordinal)];

        foreach (ExpectedHotel hotel in ExpectedHotels)
        {
            int hotelId = Assert.Single(catalogue.Hotels, row => row.Name == hotel.Name).Id;
            RoomTypeRow[] types = [.. catalogue.RoomTypes.Where(row => row.HotelId == hotelId)];

            Assert.Equal(4, types.Length);
            Assert.Equal(expectedNames, types.Select(static row => row.Name).Order(StringComparer.Ordinal).ToArray());

            foreach (ExpectedRoomType expected in ExpectedRoomTypes)
            {
                RoomTypeRow actual = Assert.Single(types, row => row.Name == expected.Name);
                Assert.Equal(expected.MaxGuests, actual.MaxGuests);
                Assert.Equal(ExpectedNightlyPrice(hotel, expected), actual.NightlyPrice);
            }
        }

        // The two worked examples of the price rule: the first hotel's base price, and the last hotel's top price.
        Assert.Equal(
            129.00m,
            Assert.Single(
                catalogue.RoomTypes,
                static row => row.HotelName == "SmartHotel Seattle Downtown" && row.Name == "Standard").NightlyPrice);
        Assert.Equal(
            369.00m,
            Assert.Single(
                catalogue.RoomTypes,
                static row => row.HotelName == "SmartHotel Chicago Loop" && row.Name == "Suite").NightlyPrice);
    }

    [Fact]
    public async Task Seed_RoomNumbers_AreFloorNumberedAndUniquePerHotel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot catalogue = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);
        IReadOnlyList<RoomRow> rooms = catalogue.Rooms;

        Assert.Equal(200, rooms.Count);

        IGrouping<int, RoomRow>[] roomsByType = [.. rooms.GroupBy(static room => room.RoomTypeId)];
        Assert.Equal(20, roomsByType.Length);
        Assert.All(roomsByType, static type => Assert.Equal(RoomsPerType, type.Count()));

        // Every hotel has the same 40 numbers: 101-110, 201-210, 301-310 and 401-410.
        string[] expectedNumbers =
        [
            .. ExpectedRoomTypes
                .SelectMany(static type => Enumerable
                    .Range(1, RoomsPerType)
                    .Select(index => ((type.Position * FloorNumberMultiplier) + index)
                        .ToString(CultureInfo.InvariantCulture)))
                .Order(StringComparer.Ordinal),
        ];

        foreach (ExpectedHotel hotel in ExpectedHotels)
        {
            int hotelId = Assert.Single(catalogue.Hotels, row => row.Name == hotel.Name).Id;

            Assert.Equal(
                expectedNumbers,
                rooms
                    .Where(room => room.HotelId == hotelId)
                    .Select(static room => room.RoomNumber)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        }

        // The floor is the room type's position, and each floor holds rooms 1 to 10.
        Assert.All(rooms, static room =>
        {
            ExpectedRoomType type = Assert.Single(ExpectedRoomTypes, expected => expected.Name == room.RoomTypeName);
            int number = int.Parse(room.RoomNumber, CultureInfo.InvariantCulture);

            Assert.Equal(type.Position, number / FloorNumberMultiplier);
            Assert.InRange(number % FloorNumberMultiplier, 1, RoomsPerType);
        });

        // IX_Rooms_HotelId_RoomNumber: no room number repeats within a hotel.
        Assert.Equal(200, rooms.Select(static room => (room.HotelId, room.RoomNumber)).Distinct().Count());

        // The denormalized hotel id always equals the room type's hotel (FK_Rooms_RoomTypes_RoomTypeId_HotelId).
        Assert.All(rooms, static room => Assert.Equal(room.RoomTypeHotelId, room.HotelId));
    }

    [Fact]
    public async Task Seed_SecondRun_ReturnsFalseAndChangesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot before = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);

        // A fresh scope with no surrounding transaction: the seeder requires an untracked context and opens its own
        // transaction.
        bool inserted = await _factory.ExecuteDbAsync(
            static (db, token) => DbSeeder.SeedAsync(db, token),
            ct);

        CatalogueSnapshot after = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);

        Assert.False(inserted);

        Assert.Equal(5, after.Hotels.Count);
        Assert.Equal(20, after.RoomTypes.Count);
        Assert.Equal(200, after.Rooms.Count);

        // Row by row, ordered by id: the second run neither added, removed nor altered a hotel, room type or room.
        Assert.Equal(before.Hotels, after.Hotels);
        Assert.Equal(before.RoomTypes, after.RoomTypes);
        Assert.Equal(before.Rooms, after.Rooms);
    }

    private static decimal ExpectedNightlyPrice(ExpectedHotel hotel, ExpectedRoomType type)
    {
        return type.BasePrice + ((hotel.Number - 1) * HotelPriceStep);
    }

    // One query per table, each ordered by id so that two reads of an unchanged catalogue compare equal row by row.
    private static async Task<CatalogueSnapshot> ReadCatalogueAsync(SmartHotelDbContext db, CancellationToken token)
    {
        List<HotelRow> hotels = await db.Hotels
            .AsNoTracking()
            .OrderBy(static hotel => hotel.Id)
            .Select(static hotel => new HotelRow(hotel.Id, hotel.Name, hotel.City, hotel.Address, hotel.Description))
            .ToListAsync(token);

        List<RoomTypeRow> roomTypes = await db.RoomTypes
            .AsNoTracking()
            .OrderBy(static type => type.Id)
            .Select(static type => new RoomTypeRow(
                type.Id,
                type.HotelId,
                type.Hotel.Name,
                type.Name,
                type.MaxGuests,
                type.NightlyPrice))
            .ToListAsync(token);

        List<RoomRow> rooms = await db.Rooms
            .AsNoTracking()
            .OrderBy(static room => room.Id)
            .Select(static room => new RoomRow(
                room.Id,
                room.HotelId,
                room.RoomTypeId,
                room.RoomNumber,
                room.RoomType.HotelId,
                room.RoomType.Name))
            .ToListAsync(token);

        return new CatalogueSnapshot(hotels, roomTypes, rooms);
    }

    private sealed record ExpectedHotel(int Number, string Name, string City);

    private sealed record ExpectedRoomType(int Position, string Name, int MaxGuests, decimal BasePrice);

    private sealed record HotelRow(int Id, string Name, string City, string Address, string Description);

    private sealed record RoomTypeRow(
        int Id,
        int HotelId,
        string HotelName,
        string Name,
        int MaxGuests,
        decimal NightlyPrice);

    private sealed record RoomRow(
        int Id,
        int HotelId,
        int RoomTypeId,
        string RoomNumber,
        int RoomTypeHotelId,
        string RoomTypeName);

    // Compared list by list, never as a whole: the lists compare by reference inside a record's equality.
    private sealed record CatalogueSnapshot(
        IReadOnlyList<HotelRow> Hotels,
        IReadOnlyList<RoomTypeRow> RoomTypes,
        IReadOnlyList<RoomRow> Rooms);
}