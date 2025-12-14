
import os
import psycopg2
from dotenv import load_dotenv

# Load env vars from .env file to get connection info
load_dotenv()

# Configuration (Ensure these match your actual setup)
# If running script from host (Mac), use NAS IP or Localhost depending on where DB is.
# Assuming based on appsettings.json logic:
DB_HOST = os.getenv("POSTGRES_HOST", "192.168.2.105") # Default to NAS as per appsettings
DB_USER = os.getenv("POSTGRES_USER", "postgres")
DB_PASS = os.getenv("POSTGRES_PASSWORD", "password")
DB_NAME = os.getenv("POSTGRES_DB", "webmusic")

print(f"Connecting to database {DB_NAME} at {DB_HOST}...")

try:
    conn = psycopg2.connect(
        host=DB_HOST,
        user=DB_USER,
        password=DB_PASS,
        dbname=DB_NAME
    )
    cursor = conn.cursor()

    # Query Lyrics table
    print("\n--- Checking Lyrics Table ---")
    cursor.execute("SELECT COUNT(*) FROM \"Lyrics\";")
    count = cursor.fetchone()[0]
    print(f"Total Lyrics Records: {count}")

    if count > 0:
        print("\n--- Latest 5 Lyrics ---")
        # Use SELECT * to avoid column name guessing issues (case sensitivity)
        cursor.execute("SELECT * FROM \"Lyrics\" LIMIT 5;")
        
        # Get column names from cursor description
        colnames = [desc[0] for desc in cursor.description]
        print(f"Columns: {colnames}")
        
        rows = cursor.fetchall()
        for row in rows:
            # Map columns by index or name
            row_dict = dict(zip(colnames, row))
            print(f"Row: {row_dict}\n")
    else:
        print("No lyrics found yet.")

    cursor.close()
    conn.close()

except Exception as e:
    print(f"Error: {e}")
    # Fallback: maybe table name is different? Case sensitive?
    print("Tip: Ensure the Lyrics table exists and you have psql dependencies.")
