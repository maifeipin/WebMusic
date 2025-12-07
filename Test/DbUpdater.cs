using System;
using Microsoft.EntityFrameworkCore;

namespace SmbDiag
{
    public static class DbUpdater
    {
        public static void UpdateSchema()
        {
            Console.WriteLine("Updating Database Schema...");
            
            using var db = new TestDbContext();
            
            // PlayHistories
            var sqlHistory = @"
                CREATE TABLE IF NOT EXISTS ""PlayHistories"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_PlayHistories"" PRIMARY KEY AUTOINCREMENT,
                    ""UserId"" INTEGER NOT NULL,
                    ""MediaFileId"" INTEGER NOT NULL,
                    ""PlayedAt"" TEXT NOT NULL,
                    CONSTRAINT ""FK_PlayHistories_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_PlayHistories_MediaFiles_MediaFileId"" FOREIGN KEY (""MediaFileId"") REFERENCES ""MediaFiles"" (""Id"") ON DELETE CASCADE
                );";
            try {
                db.Database.ExecuteSqlRaw(sqlHistory);
                Console.WriteLine(" - PlayHistories table check/create: OK");
            } catch (Exception ex) {
                Console.WriteLine($" - PlayHistories Error: {ex.Message}");
            }

            // Favorites
            var sqlFavorites = @"
                CREATE TABLE IF NOT EXISTS ""Favorites"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Favorites"" PRIMARY KEY AUTOINCREMENT,
                    ""UserId"" INTEGER NOT NULL,
                    ""MediaFileId"" INTEGER NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL,
                    CONSTRAINT ""FK_Favorites_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_Favorites_MediaFiles_MediaFileId"" FOREIGN KEY (""MediaFileId"" ) REFERENCES ""MediaFiles"" (""Id"") ON DELETE CASCADE
                );";
            try {
                db.Database.ExecuteSqlRaw(sqlFavorites);
                Console.WriteLine(" - Favorites table check/create: OK");
            } catch (Exception ex) {
                Console.WriteLine($" - Favorites Error: {ex.Message}");
            }
            
            Console.WriteLine("Schema Update Complete.");
        }
    }
}
