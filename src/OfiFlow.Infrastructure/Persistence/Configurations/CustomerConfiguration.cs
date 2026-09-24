using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Type)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(Customer.NameMaxLength);

        builder.Property(c => c.Email)
            .HasConversion(
                email => email == null ? null : email.Value,
                value => value == null ? null : Email.Create(value))
            .HasMaxLength(Email.MaxLength);

        builder.Property(c => c.Phone)
            .HasConversion(
                phone => phone == null ? null : phone.Value,
                value => value == null ? null : PhoneNumber.Create(value))
            .HasMaxLength(PhoneNumber.MaxLength);

        builder.Property(c => c.Address).HasMaxLength(Customer.AddressMaxLength);
        builder.Property(c => c.Notes).HasMaxLength(Customer.NotesMaxLength);
    }
}
