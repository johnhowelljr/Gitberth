using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Gitberth.Data;

public class GitberthDb(DbContextOptions<GitberthDb> options) : DbContext(options)
{
    public DbSet<AppDefinition> Apps => Set<AppDefinition>();
    public DbSet<PortMapping> Ports => Set<PortMapping>();
    public DbSet<EnvVar> EnvVars => Set<EnvVar>();
    public DbSet<VolumeMount> Volumes => Set<VolumeMount>();
    public DbSet<CustomDomain> Domains => Set<CustomDomain>();
    public DbSet<DomainRemoval> DomainRemovals => Set<DomainRemoval>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AppNetwork> AppNetworks => Set<AppNetwork>();
    public DbSet<KeptNetwork> KeptNetworks => Set<KeptNetwork>();
    public DbSet<ContainerPortNote> ContainerPortNotes => Set<ContainerPortNote>();
    public DbSet<ContainerChangeEntry> ContainerChanges => Set<ContainerChangeEntry>();
    public DbSet<RegistryCredential> Registries => Set<RegistryCredential>();
    public DbSet<ImageCheck> ImageChecks => Set<ImageCheck>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();
    public DbSet<PreviewInfo> Previews => Set<PreviewInfo>();
    public DbSet<DeployHook> DeployHooks => Set<DeployHook>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppDefinition>(e =>
        {
            e.ToTable("Apps");
            e.HasIndex(a => a.Slug).IsUnique();
            // SQLite lets any number of rows have no Container: every app Gitberth creates is gb-<slug>.
            e.HasIndex(a => a.Container).IsUnique();
            e.Property(a => a.DockerfileMode).HasConversion<string>();
            e.Property(a => a.DesiredState).HasConversion<string>();
            e.Property(a => a.Source).HasConversion<string>().HasDefaultValue(Gitberth.Apps.AppSource.Git).HasSentinel(Gitberth.Apps.AppSource.Git);
            e.Property(a => a.UpdatePolicy).HasConversion<string>().HasDefaultValue(Gitberth.Apps.UpdatePolicy.Off).HasSentinel(Gitberth.Apps.UpdatePolicy.Off);
            e.Property(a => a.PreviewMode).HasConversion<string>().HasDefaultValue(PreviewMode.Off).HasSentinel(PreviewMode.Off);
            // A preview's row names its parent without a foreign key, for the same reason as RegistryId below.
            e.HasIndex(a => a.ParentAppId);
            // RegistryId names a credential without a foreign key: adding one would make EF Core rebuild Apps, which five
            // tables reference (RegistryService clears it on the apps that name a credential it removes).
            e.HasMany(a => a.Ports).WithOne().HasForeignKey(p => p.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.EnvVars).WithOne().HasForeignKey(v => v.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Volumes).WithOne().HasForeignKey(v => v.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Domains).WithOne().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Networks).WithOne().HasForeignKey(n => n.AppId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PortMapping>().Property(p => p.Access).HasConversion<string>();
        b.Entity<AppNetwork>(e =>
        {
            e.ToTable("AppNetworks");
            e.HasIndex(n => new { n.AppId, n.Network }).IsUnique();
        });
        b.Entity<KeptNetwork>(e =>
        {
            e.ToTable("KeptNetworks");
            e.HasKey(k => new { k.ContainerName, k.Network });
        });
        b.Entity<ContainerPortNote>(e =>
        {
            e.ToTable("ContainerPortNotes");
            e.HasKey(n => new { n.ContainerName, n.Port, n.Protocol });
        });
        b.Entity<ContainerChangeEntry>(e =>
        {
            e.ToTable("ContainerChanges");
            e.HasIndex(c => c.ContainerName);
            e.Property(c => c.Step).HasConversion<string>();
            e.Property(c => c.Outcome).HasConversion<string>();
            e.Property(c => c.Kind).HasConversion<string>().HasDefaultValue(Gitberth.Apps.ChangeKind.Ports).HasSentinel(Gitberth.Apps.ChangeKind.Ports);
        });
        b.Entity<RegistryCredential>(e =>
        {
            e.ToTable("Registries");
            e.HasIndex(r => r.Host).IsUnique();
        });
        b.Entity<ImageCheck>(e =>
        {
            e.ToTable("ImageChecks");
            e.HasKey(c => c.Reference);
        });
        b.Entity<ActivityEntry>(e =>
        {
            e.ToTable("ActivityEntries");
            e.HasIndex(a => new { a.ContainerName, a.At });
            e.Property(a => a.Kind).HasConversion<string>();
            e.Property(a => a.Tone).HasConversion<string>();
            // An entry outlives its app's row: a forgotten app's history still reads by its container's name.
            e.HasOne<AppDefinition>().WithMany().HasForeignKey(a => a.AppId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<EnvVar>(e =>
        {
            // One value per name and scope: All, Production and Preview can each have one.
            e.HasIndex(v => new { v.AppId, v.Key, v.Scope }).IsUnique();
            e.Property(v => v.Scope).HasConversion<string>().HasDefaultValue(EnvScope.All).HasSentinel(EnvScope.All);
        });
        b.Entity<PreviewInfo>(e =>
        {
            e.ToTable("Previews");
            e.HasKey(p => p.AppId);
            e.HasIndex(p => p.ParentAppId);
            e.Property(p => p.Kind).HasConversion<string>();
            // Goes with the preview's own row. The table that has the key changes, not Apps.
            e.HasOne<AppDefinition>().WithOne().HasForeignKey<PreviewInfo>(p => p.AppId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CustomDomain>(e =>
        {
            e.ToTable("Domains");
            e.HasIndex(d => d.Hostname).IsUnique();
        });
        b.Entity<DomainRemoval>(e =>
        {
            e.ToTable("DomainRemovals");
            e.HasKey(r => r.Hostname);
        });
        b.Entity<Deployment>(e =>
        {
            e.HasIndex(d => d.AppId);
            e.Property(d => d.Status).HasConversion<string>();
            e.Property(d => d.Trigger).HasConversion<string>();
            e.HasOne<AppDefinition>().WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DeployHook>(e =>
        {
            e.ToTable("DeployHooks");
            e.HasIndex(h => h.TokenHash).IsUnique();
            e.HasIndex(h => h.AppId);
            // Goes with its app. The table that has the key changes, not Apps.
            e.HasOne<AppDefinition>().WithMany().HasForeignKey(h => h.AppId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Setting>().HasKey(s => s.Key);

        // SQLite hands DateTime back as Unspecified; everything is stored as UTC.
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
                else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
            }
        }
    }
}

/// <summary>Lets `dotnet ef` create migrations without running Program.cs.</summary>
public sealed class DesignTimeDbFactory : IDesignTimeDbContextFactory<GitberthDb>
{
    public GitberthDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<GitberthDb>().UseSqlite("Data Source=design-time.db").Options);
}
