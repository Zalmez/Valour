using Microsoft.EntityFrameworkCore;
using Valour.Shared.Models.Economy;

namespace Valour.Database.Economy;

/// <summary>
/// A transaction represents a *completed* transaction between two accounts.
/// </summary>
public class Transaction : ISharedTransaction
{
    public virtual Planet Planet { get; set; }
    public virtual User UserFrom { get; set; }
    public virtual User UserTo { get; set; }
    public virtual EcoAccount AccountFrom { get; set; }
    public virtual EcoAccount AccountTo { get; set; }

    /// <summary>
    /// Unlike most ids in Valour, transactions do not use a snowflake.
    /// We anticipate some rapid transactions via market botting, which
    /// could potentially hit our snowflake id-per-second limit.
    /// Instead we use a Guid
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// The planet the transaction belongs to
    /// </summary>
    public long PlanetId { get; set; }

    /// <summary>
    /// The id of the owner of the sending account
    /// </summary>
    public long UserFromId { get; set; }

    /// <summary>
    /// The id of the sending account
    /// </summary>
    public long AccountFromId { get; set; }

    /// <summary>
    /// The id of the owner of the receiving account
    /// </summary>
    public long UserToId { get; set; }

    /// <summary>
    /// The id of the receiving account
    /// </summary>
    public long AccountToId { get; set; }

    /// <summary>
    /// The time this transaction was completed
    /// </summary>
    public DateTime TimeStamp { get; set; }

    /// <summary>
    /// A description of the transaction
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// The amount of currency transferred in the transaction
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Additional data that can be attached to a transaction
    /// </summary>
    public string Data { get; set; }

    /// <summary>
    /// A value that can be used to identify a transaction completing.
    /// It should match the request fingerprint.
    /// </summary>
    public string Fingerprint { get; set; }

    /// <summary>
    /// If this transaction was forced by an Eco Admin, this is the id of the user who forced it.
    /// </summary>
    public long? ForcedBy { get; set; }

    public static void SetupDbModel(ModelBuilder builder)
    {
        builder.Entity<Transaction>(e =>
        {
            // Table
            e.ToTable("transactions");

            // Keys
            e.HasKey(x => x.Id);

            // Properties
            e.Property(x => x.Id)
                .HasColumnName("id");

            e.Property(x => x.PlanetId)
                .HasColumnName("planet_id");

            e.Property(x => x.UserFromId)
                .HasColumnName("user_from_id");

            e.Property(x => x.AccountFromId)
                .HasColumnName("account_from_id");

            e.Property(x => x.UserToId)
                .HasColumnName("user_to_id");

            e.Property(x => x.AccountToId)
                .HasColumnName("account_to_id");

            e.Property(x => x.TimeStamp)
                .HasColumnName("time_stamp");

            e.Property(x => x.Description)
                .HasColumnName("description");

            e.Property(x => x.Amount)
                .HasColumnName("amount");

            e.Property(x => x.Data)
                .HasColumnName("data");

            e.Property(x => x.Fingerprint)
                .HasColumnName("fingerprint");

            e.Property(x => x.ForcedBy)
                .HasColumnName("forced_by");

            // Relationships
            e.HasOne(x => x.Planet)
                .WithMany()
                .HasForeignKey(x => x.PlanetId);

            e.HasOne(x => x.UserFrom)
                .WithMany()
                .HasForeignKey(x => x.UserFromId);

            e.HasOne(x => x.UserTo)
                .WithMany()
                .HasForeignKey(x => x.UserToId);

            e.HasOne(x => x.AccountFrom)
                .WithMany()
                .HasForeignKey(x => x.AccountFromId);

            e.HasOne(x => x.AccountTo)
                .WithMany()
                .HasForeignKey(x => x.AccountToId);
            
            // prevent concurrent webhook deliveries
            e.HasIndex(x => x.Fingerprint)
                .IsUnique();
        });
    }
}
