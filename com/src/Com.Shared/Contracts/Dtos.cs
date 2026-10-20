namespace Com.Shared.Contracts;

public record LoginRequest(string Upn, string Password);
public record LoginResponse(string Token, UserDto User);
public record UserDto(string Upn, string Name, string Role, string RoleLabel);

public record CustomerDto(Guid Id, string Code, string Name, string TaxId, decimal CreditLimit);
public record ProductDto(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency);

public record CreateOrderLineRequest(Guid ProductId, int Qty, decimal? UnitPrice);
public record CreateOrderRequest(Guid CustomerId, string? Terms, string? Remark,
                                 List<CreateOrderLineRequest> Lines, string? IdempotencyKey);

public record OrderLineDto(int LineNo, string ProductName, int Qty, decimal UnitPrice, decimal Amount);

public record ApprovalStepDto(int Level, string Role, string RoleLabel, string ApproverUpn,
                              string Decision, string? Comment, DateTimeOffset? DecidedAt,
                              DateTimeOffset DueAt, bool Overdue);

public record ContractDto(Guid Id, string Status, string? Sha256, long SizeBytes, int Version,
                          string? BlobPath, DateTimeOffset? ImmutableUntil, DateTimeOffset CreatedAt,
                          string? Error, string? SasUrl);

public record TimelineItemDto(string Action, string? From, string? To, string Actor, DateTimeOffset At, string TraceId);

public record OrderListItemDto(Guid Id, string OrderNo, string CustomerName, string Status, string StatusLabel,
                               decimal TotalAmount, string Currency, DateTimeOffset CreatedAt,
                               string ApprovalStage, string CreatedBy);

public record OrderDetailDto(Guid Id, string OrderNo, Guid CustomerId, string CustomerName, string CustomerCode,
                             string Status, string StatusLabel, decimal TotalAmount, string Currency, string Terms,
                             string? Remark, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
                             long Version, string ApprovalRule, List<OrderLineDto> Lines,
                             List<ApprovalStepDto> Approvals, ContractDto? Contract, bool CanSubmit, bool CanApprove);

public record PendingApprovalDto(Guid TaskId, Guid OrderId, string OrderNo, string CustomerName, decimal TotalAmount,
                                 string Currency, int Level, int MaxLevel, DateTimeOffset DueAt, DateTimeOffset CreatedAt,
                                 bool Overdue, string Rule);

public record DecisionRequest(string Decision, string? Comment);
public record BatchDecisionRequest(List<Guid> TaskIds, string Decision, string? Comment);

public record IntakeUploadResponse(Guid Id, string FileName, string Status);
public record IntakeExtractedField(string Field, string Label, string Value, double Confidence, string Source);
public record IntakeDto(Guid Id, string FileName, string Status, double Confidence,
                        Guid? DraftOrderId, List<IntakeExtractedField> Fields, DateTimeOffset CreatedAt, string? Error);

public record DepStatusDto(string Name, string Kind, bool Ok, long LatencyMs, string Detail);
public record OpsSummaryDto(string Environment, string Region, string Version,
                            int OrderCount, int PendingApprovals, int ArchivedContracts,
                            List<DepStatusDto> Dependencies, DateTimeOffset ServerTime,
                            Dictionary<string, string> Modes);

public record ApiError(string Code, string Message, string TraceId, object? Errors = null);
