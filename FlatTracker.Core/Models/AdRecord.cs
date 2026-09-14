namespace FlatTracker.Core.Models;

public sealed class AdRecord
{
    public long Id { get; set; }
    public long AdId { get; set; }
    public string AdLink { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal? PriceByn { get; set; }
    public decimal? PriceUsd { get; set; }
    public string? Currency { get; set; }
    public int? Rooms { get; set; }
    public double? TotalArea { get; set; }
    public int? Floor { get; set; }
    public string? Region { get; set; }
    public string? District { get; set; }
    public string? Metro { get; set; }
    public string? Address { get; set; }
    public bool IsCompany { get; set; }
    public int ImageCount { get; set; }
    public DateTime LastUpdateTime { get; set; }
    public DateTime FirstSeen { get; set; }
}