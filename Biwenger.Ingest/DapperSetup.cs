using System.Data;
using System.Runtime.CompilerServices;
using Dapper;

namespace Biwenger.Ingest;

/// <summary>
/// Dapper no soporta DateOnly de forma nativa. El handler se registra en un
/// inicializador de módulo para que esté disponible sea cual sea el punto de
/// entrada — sin depender de que Program.cs llegue a una línea concreta.
/// </summary>
internal static class DapperSetup
{
    [ModuleInitializer]
    internal static void Register()
        => SqlMapper.AddTypeHandler(new DateOnlyHandler());

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value)
            => value switch
            {
                DateTime dt => DateOnly.FromDateTime(dt),
                DateOnly d => d,
                string s => DateOnly.Parse(s),
                _ => throw new InvalidCastException(
                        $"No se puede convertir {value?.GetType().Name ?? "null"} a DateOnly.")
            };
    }
}