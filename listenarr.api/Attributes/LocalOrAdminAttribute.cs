using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Listenarr.Api.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class LocalOrAdminAttribute : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var isLoopback = HttpSecurityRequestUtils.IsLoopbackRequest(context.HttpContext);
            var isAuth = HttpSecurityRequestUtils.IsAuthenticatedAdminOrApiKey(context.HttpContext);

            if (!isLoopback && !isAuth)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            }
        }
    }
}
