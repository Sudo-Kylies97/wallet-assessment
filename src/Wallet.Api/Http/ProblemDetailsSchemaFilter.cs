using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Wallet.Api.Http;

// Both handlers emit code/traceId extensions; model-validation failures also emit Errors.
public sealed class ProblemDetailsSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema input, SchemaFilterContext context)
    {
        if (context.Type != typeof(ProblemDetails) || input is not OpenApiSchema schema) return;
        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Properties["code"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String, Description = "Stable machine-readable error code."
        };
        schema.Properties["traceId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String, Description = "Request identifier for correlating server logs."
        };
        schema.Properties["errors"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object, Description = "Field errors, present for model-validation failures.",
            AdditionalProperties = new OpenApiSchema
            {
                Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String }
            }
        };
        schema.Required ??= new HashSet<string>();
        schema.Required.Add("code");
        schema.Required.Add("traceId");
    }
}
