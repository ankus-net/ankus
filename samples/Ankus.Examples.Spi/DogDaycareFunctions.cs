[assembly: PgSql("spi_srf.table", """
    CREATE TABLE spi_srf.dog_daycare (dog_name varchar(256), dog_age integer, dog_breed varchar(256));
    INSERT INTO spi_srf.dog_daycare (dog_name, dog_age, dog_breed) VALUES
        ('Fido', 3, 'Labrador'), ('Spot', 5, 'Poodle'), ('Rover', 7, 'Golden Retriever'),
        ('Snoopy', 9, 'Beagle'), ('Lassie', 11, 'Collie'), ('Scooby', 13, 'Great Dane'),
        ('Moomba', 15, 'Labrador');
    """, Requires = ["spi_srf.schema"])]

namespace Ankus.Examples.Spi;

/// <summary>
/// Ports pgrx's SPI table functions using named C# tuples.
/// </summary>
[PgSchema("spi_srf", Id = "spi_srf.schema")]
public static class DogDaycareFunctions
{
    /// <summary>
    /// Maps every dog to a checked age calculation before leaving the SPI session.
    /// </summary>
    /// <returns>Owned names, ages, breeds and ages multiplied by seven.</returns>
    [PgFunction]
    public static IEnumerable<(string? DogName, int DogAge, string? DogBreed, int HumanAge)> CalculateHumanYears()
        => Ankus.Spi.Connect(static session => session.Select("SELECT dog_name, dog_age, dog_breed FROM spi_srf.dog_daycare")
            .Select(static row =>
            {
                int age = row.Get<int>("dog_age");
                return (row.Get<string?>("dog_name"), age, row.Get<string?>("dog_breed"), checked(age * 7));
            }).ToArray());

    /// <summary>
    /// Filters by a bound breed while preserving nullable source columns.
    /// </summary>
    /// <param name="breed">The exact breed to find.</param>
    /// <returns>Owned matching rows with their original SQL NULL values.</returns>
    [PgFunction]
    public static IEnumerable<(string? DogName, int? DogAge, string? DogBreed)> FilterByBreed(string breed)
        => Ankus.Spi.Connect(session => session.Select(Ankus.Spi.Sql($"SELECT dog_name, dog_age, dog_breed FROM spi_srf.dog_daycare WHERE dog_breed = {breed}"))
            .Select(static row => (row.Get<string?>("dog_name"), row.Get<int?>("dog_age"), row.Get<string?>("dog_breed"))).ToArray());
}
