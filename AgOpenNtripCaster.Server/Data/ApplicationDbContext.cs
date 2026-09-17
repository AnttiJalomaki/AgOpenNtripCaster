using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AgOpenNtripCaster.Server.Models.Entities;

namespace AgOpenNtripCaster.Server.Data;

public class ApplicationDbContext : IdentityDbContext<NtripUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    // DbSets
    public DbSet<NtripGroup> NtripGroups { get; set; } = null!;
    public DbSet<MountPoint> MountPoints { get; set; } = null!;
    public DbSet<ClientSession> ClientSessions { get; set; } = null!;
    public DbSet<SourceConnection> SourceConnections { get; set; } = null!;
    public DbSet<Activity> Activities { get; set; } = null!;
    public DbSet<CasterInfo> CasterInfos { get; set; } = null!;
    public DbSet<NetworkInfo> NetworkInfos { get; set; } = null!;
    public DbSet<EmailTriggerSettings> EmailTriggerSettings { get; set; } = null!;
    public DbSet<EmailSmtpSettings> EmailSmtpSettings { get; set; } = null!;
    public DbSet<TelegramSettings> TelegramSettings { get; set; } = null!;
    public DbSet<PerformanceMetric> PerformanceMetrics { get; set; } = null!;
    public DbSet<DiagnosticEvent> DiagnosticEvents { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Configure NTRIP user
        builder.Entity<NtripUser>()
            .HasMany(u => u.Groups)
            .WithMany(g => g.Users)
            .UsingEntity(j => j.ToTable("UserGroups"));

        builder.Entity<NtripUser>()
            .HasMany(u => u.ClientSessions)
            .WithOne(c => c.User)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure NtripGroup
        builder.Entity<NtripGroup>()
            .HasMany(g => g.MountPoints)
            .WithMany(m => m.AllowedGroups)
            .UsingEntity(j => j.ToTable("GroupMountPoints"));

        // Configure MountPoint
        builder.Entity<MountPoint>()
            .HasMany(m => m.ClientSessions)
            .WithOne(c => c.MountPoint)
            .HasForeignKey(c => c.MountPointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<MountPoint>()
            .HasMany(m => m.SourceConnections)
            .WithOne(s => s.MountPoint)
            .HasForeignKey(s => s.MountPointId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure ClientSession
        builder.Entity<ClientSession>()
            .HasKey(c => c.Id);

        builder.Entity<ClientSession>()
            .HasOne(c => c.User)
            .WithMany(u => u.ClientSessions)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<ClientSession>()
            .HasIndex(c => c.ConnectedAt);

        builder.Entity<ClientSession>()
            .HasIndex(c => new { c.UserId, c.ConnectedAt });

        // Configure SourceConnection
        builder.Entity<SourceConnection>()
            .HasOne(s => s.MountPoint)
            .WithMany(m => m.SourceConnections)
            .HasForeignKey(s => s.MountPointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SourceConnection>()
            .HasIndex(s => s.ConnectedAt);

        // Configure Activity
        builder.Entity<Activity>()
            .HasOne(a => a.MountPoint)
            .WithMany()
            .HasForeignKey(a => a.MountPointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Activity>()
            .HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure DiagnosticEvent
        builder.Entity<DiagnosticEvent>()
            .HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<DiagnosticEvent>()
            .HasOne(e => e.ClientSession)
            .WithMany()
            .HasForeignKey(e => e.ClientSessionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<DiagnosticEvent>()
            .HasOne(e => e.SourceConnection)
            .WithMany()
            .HasForeignKey(e => e.SourceConnectionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<DiagnosticEvent>()
            .HasOne(e => e.MountPoint)
            .WithMany()
            .HasForeignKey(e => e.MountPointId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<DiagnosticEvent>()
            .HasIndex(e => e.Timestamp);

        builder.Entity<DiagnosticEvent>()
            .HasIndex(e => e.Kind);

        builder.Entity<DiagnosticEvent>()
            .HasIndex(e => e.UserId);

        builder.Entity<DiagnosticEvent>()
            .HasIndex(e => e.ClientSessionId);

        builder.Entity<DiagnosticEvent>()
            .HasIndex(e => e.MountPointId);
    }
}
