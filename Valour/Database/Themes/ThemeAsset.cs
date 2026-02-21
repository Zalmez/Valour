using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Valour.Database.Themes;

[Table("theme_assets")]
public class ThemeAsset
{
    [Column("id")]
    public long Id { get; set; }

    [Column("theme_id")]
    public long ThemeId { get; set; }

    [Column("name")]
    public string Name { get; set; }

    [ForeignKey("ThemeId")]
    [JsonIgnore]
    public virtual Theme Theme { get; set; }
}
