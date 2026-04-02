using Microsoft.EntityFrameworkCore;
using Moq;

namespace Listenarr.Api.Tests
{
    public class BaseTests
    {
        /// <summary>
        /// Returns an empty database
        /// </summary>
        public static IDbContextFactory<ListenArrDbContext> CreateDB()
        {
            return CreateDB(db => {});
        }

        /// <summary>
        /// Returns a database initialized using the provided method
        /// </summary>
        public static IDbContextFactory<ListenArrDbContext> CreateDB(Action<ListenArrDbContext> initDb)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            
            using var context = new ListenArrDbContext(options);
            initDb(context);
            context.SaveChanges();

            var mockFactory = new Mock<IDbContextFactory<ListenArrDbContext>>();
            
            mockFactory
                .Setup(factory => factory.CreateDbContext())
                .Returns(() => new ListenArrDbContext(options));
            
            return mockFactory.Object;
        }
    }
}