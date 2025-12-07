
namespace WebMusic.Backend.Models;

public class PagedResult<T>
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public required IEnumerable<T> Files { get; set; }
}
