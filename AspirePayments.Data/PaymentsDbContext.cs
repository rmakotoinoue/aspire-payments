using AspirePayments.Data.Domain;
using AspirePayments.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AspirePayments.Data;

/// <summary>Contexto EF Core do fluxo de pagamentos.</summary>
public class PaymentsDbContext : DbContext
{
    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

    public DbSet<PaymentProcessingRecord> PaymentProcessingRecords => Set<PaymentProcessingRecord>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(e => e.OrderId);
            entity.Property(e => e.OrderId).HasColumnName("order_id").ValueGeneratedNever();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(8);
            entity.Property(e => e.PaymentMethod).HasColumnName("payment_method").HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.TraceId).HasColumnName("trace_id").HasMaxLength(64);
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.ToTable("payment_transactions");
            entity.HasKey(e => e.PaymentTransactionId);
            entity.Property(e => e.PaymentTransactionId).HasColumnName("payment_transaction_id").ValueGeneratedNever();
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200);

            // Garantia no banco (e não só na aplicação) de que uma chave
            // lógica de pagamento nunca produz duas transações.
            entity.HasIndex(e => e.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("ux_payment_transactions_idempotency_key");

            entity.Property(e => e.Amount).HasColumnName("amount").HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(8);
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.PaymentMethod).HasColumnName("payment_method").HasMaxLength(64);
            entity.Property(e => e.Scenario).HasColumnName("scenario").HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.FailureReason).HasColumnName("failure_reason").HasMaxLength(512);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
            entity.Property(e => e.Version).HasColumnName("version").IsConcurrencyToken();

            entity.HasOne(e => e.Order)
                .WithMany(o => o.PaymentTransactions)
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PaymentProcessingRecord>(entity =>
        {
            entity.ToTable("payment_processing_records");
            entity.HasKey(e => e.ProcessingRecordId);
            entity.Property(e => e.ProcessingRecordId).HasColumnName("processing_record_id").ValueGeneratedNever();
            entity.Property(e => e.PaymentTransactionId).HasColumnName("payment_transaction_id");
            entity.Property(e => e.MessageId).HasColumnName("message_id").HasMaxLength(200);
            entity.Property(e => e.ProcessingStatus).HasColumnName("processing_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.AttemptNumber).HasColumnName("attempt_number");
            entity.Property(e => e.ErrorDetails).HasColumnName("error_details").HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            // Impede que a mesma mensagem produza dois registros no MESMO
            // estado (por exemplo, dois "Succeeded"), o que duplicaria a
            // evidência de conclusão. O status entra na chave porque uma
            // mesma mensagem gera um registro "Processing" e um final.
            entity.HasIndex(e => new { e.PaymentTransactionId, e.MessageId, e.ProcessingStatus })
                .IsUnique()
                .HasDatabaseName("ux_payment_processing_records_message");

            entity.HasOne(e => e.PaymentTransaction)
                .WithMany(t => t.ProcessingRecords)
                .HasForeignKey(e => e.PaymentTransactionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.ToTable("refunds");
            entity.HasKey(e => e.RefundId);
            entity.Property(e => e.RefundId).HasColumnName("refund_id").ValueGeneratedNever();
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.PaymentTransactionId).HasColumnName("payment_transaction_id");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200);

            // Um pedido de reembolso lógico só pode ser executado uma vez.
            entity.HasIndex(e => e.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("ux_refunds_idempotency_key");

            entity.Property(e => e.Amount).HasColumnName("amount").HasPrecision(18, 2);
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(512);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");

            entity.HasOne(e => e.Order)
                .WithMany(o => o.Refunds)
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PaymentTransaction)
                .WithMany(t => t.Refunds)
                .HasForeignKey(e => e.PaymentTransactionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(e => e.OutboxMessageId);
            entity.Property(e => e.OutboxMessageId).HasColumnName("outbox_message_id").ValueGeneratedNever();
            entity.Property(e => e.MessageId).HasColumnName("message_id").HasMaxLength(200);
            entity.Property(e => e.MessageType).HasColumnName("message_type").HasMaxLength(128);
            entity.Property(e => e.Exchange).HasColumnName("exchange").HasMaxLength(200);
            entity.Property(e => e.RoutingKey).HasColumnName("routing_key").HasMaxLength(200);
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(e => e.Headers).HasColumnName("headers").HasColumnType("jsonb");
            entity.Property(e => e.TraceParent).HasColumnName("trace_parent").HasMaxLength(128);
            entity.Property(e => e.TraceState).HasColumnName("trace_state").HasMaxLength(512);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.PublishAttempts).HasColumnName("publish_attempts");
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(2000);

            // Índice parcial: só as mensagens ainda não publicadas são
            // candidatas ao dispatcher da outbox.
            entity.HasIndex(e => e.PublishedAt)
                .HasDatabaseName("ix_outbox_messages_unpublished");
        });
    }
}
