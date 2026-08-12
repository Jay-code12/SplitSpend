using FluentValidation;
using SplitSpend.UserService.Application.DTOs;
using SplitSpend.UserService.Domain.Enums;

namespace SplitSpend.UserService.Application.Validators;

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(100).WithMessage("First name must not exceed 100 characters.")
            .Matches("^[a-zA-Z '-]+$").WithMessage("First name contains invalid characters.");

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(100).WithMessage("Last name must not exceed 100 characters.")
            .Matches("^[a-zA-Z '-]+$").WithMessage("Last name contains invalid characters.");

        RuleFor(x => x.Phone)
            .Matches(@"^\+?[0-9\s\-\(\)]{7,20}$")
            .When(x => x.Phone is not null)
            .WithMessage("Phone number is invalid.");

        When(x => x.Profile is not null, () =>
        {
            RuleFor(x => x.Profile!.Bio)
                .MaximumLength(500)
                .When(x => x.Profile!.Bio is not null);

            RuleFor(x => x.Profile!.DateOfBirth)
                .LessThan(DateTime.UtcNow.AddYears(-13))
                .When(x => x.Profile!.DateOfBirth is not null)
                .WithMessage("User must be at least 13 years old.");

            RuleFor(x => x.Profile!.AvatarUrl)
                .MaximumLength(2048)
                .When(x => x.Profile!.AvatarUrl is not null);
        });

        When(x => x.VendorProfile is not null, () =>
        {
            RuleFor(x => x.VendorProfile!.BusinessName)
                .NotEmpty().WithMessage("Business name is required for vendor profiles.")
                .MaximumLength(200);

            RuleFor(x => x.VendorProfile!.BusinessType)
                .MaximumLength(100)
                .When(x => x.VendorProfile!.BusinessType is not null);
        });
    }
}

public sealed class AssignRoleRequestValidator : AbstractValidator<AssignRoleRequest>
{
    private static readonly string[] ValidRoles =
        Enum.GetNames<UserRole>().Select(r => r.ToLower()).ToArray();

    public AssignRoleRequestValidator()
    {
        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .Must(r => ValidRoles.Contains(r.ToLower()))
            .WithMessage($"Role must be one of: {string.Join(", ", Enum.GetNames<UserRole>())}.");
    }
}
