using EOS.API.Data;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Admin;

/// <summary>Production changeset writer: delegates to FieldAdminRepository management endpoints.</summary>
public sealed class FieldMetaWriter(FieldAdminRepository fieldAdmin) : IChangeSetWriter
{
    public Task RegisterTableAsync(string tableId, string description, string? kind, string updatedBy, CancellationToken token) =>
        fieldAdmin.CreateTableAsync(
            new(tableId, new(description, kind, null, null)), updatedBy, token);

    public async Task<(int Created, int Skipped, IReadOnlyList<string> Reasons)> AddFieldsAsync(
        string tableId, IReadOnlyList<string> fieldIds, string updatedBy, CancellationToken token)
    {
        var result = await fieldAdmin.CreateUnmanagedFieldsAsync(new(tableId, fieldIds), updatedBy, token);
        return (result.Created, result.Skipped, result.SkippedReasons);
    }
}
