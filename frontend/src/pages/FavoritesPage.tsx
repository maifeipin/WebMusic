import { useEffect, useState } from 'react';
import { getFavorites, toggleFavorite } from '../services/api';
import { usePlayer, type Song } from '../context/PlayerContext';
import { Play, Heart } from 'lucide-react';
import { formatRelativeTime } from '../utils/time';
import { Pagination } from '../components/Pagination';

export default function FavoritesPage() {
    const { playSong, playQueue } = usePlayer();
    const [favorites, setFavorites] = useState<{ createdAt: string, song: Song }[]>([]);
    const [loading, setLoading] = useState(true);
    const [page, setPage] = useState(1);
    const [total, setTotal] = useState(0);
    const [pageSize, setPageSize] = useState(20);

    useEffect(() => {
        loadData(page, pageSize);
    }, [page, pageSize]);

    const loadData = async (currentPage: number, size: number) => {
        try {
            setLoading(true);
            const res = await getFavorites(currentPage, size);
            const mapped = res.data.items.map((item: any) => ({
                createdAt: item.createdAt,
                song: {
                    ...item.mediaFile,
                    duration: item.mediaFile.duration || 0
                }
            }));
            setFavorites(mapped);
            setTotal(res.data.total);
        } catch (error) {
            console.error(error);
        } finally {
            setLoading(false);
        }
    };

    const handlePageSizeChange = (size: number) => {
        setPageSize(size);
        setPage(1); // Reset to first page
    };

    const handleRemove = async (id: number, e: React.MouseEvent) => {
        e.stopPropagation();
        await toggleFavorite(id);
        setFavorites(prev => prev.filter(s => s.song.id !== id));
        // Optional: reload if empty?
    };

    return (
        <div className="p-4 md:p-8 pb-32 max-w-5xl mx-auto h-full box-border">
            <header className="flex items-center justify-between mb-8">
                <div className="flex items-center gap-4">
                    <div className="p-3 bg-pink-500/20 rounded-xl text-pink-400">
                        <Heart size={24} fill="currentColor" />
                    </div>
                    <div>
                        <h1 className="text-3xl font-bold text-white">Favorites</h1>
                        <p className="text-gray-400 text-sm">Your loved tracks</p>
                    </div>
                </div>
                <button
                    onClick={() => {
                        if (favorites.length > 0) {
                            const queue = favorites.map(f => f.song);
                            playQueue(queue, 0);
                        }
                    }}
                    disabled={favorites.length === 0}
                    className="flex items-center gap-2 px-4 py-2 bg-pink-600 hover:bg-pink-500 disabled:bg-gray-800 disabled:text-gray-500 disabled:cursor-not-allowed text-white rounded-lg font-medium transition shadow-lg shadow-pink-900/20"
                >
                    <Play size={18} fill="currentColor" />
                    <span className="hidden md:inline">Play All</span>
                </button>
            </header>

            {loading ? (
                <div className="text-center text-gray-500 py-20">Loading...</div>
            ) : favorites.length === 0 ? (
                <div className="text-center text-gray-500 py-20 bg-gray-900/50 rounded-2xl border border-gray-800">No favorites yet. Go mark some songs with ❤️!</div>
            ) : (
                <div className="space-y-1">
                    {/* Header */}
                    <div className="grid grid-cols-[40px_minmax(120px,1fr)_40px] md:grid-cols-[40px_1fr_100px_40px_40px] gap-2 md:gap-4 px-2 md:px-4 py-2 text-xs font-semibold text-gray-500 uppercase tracking-wider">
                        <div className="text-center">#</div>
                        <div>Title</div>
                        <div className="hidden md:block text-right">Time</div>
                        <div className="text-center md:block hidden">Like</div>
                        <div></div>
                    </div>

                    {favorites.map((item) => (
                        <div
                            key={item.song.id}
                            onClick={() => playSong(item.song)}
                            className="group grid grid-cols-[40px_minmax(120px,1fr)_40px] md:grid-cols-[40px_1fr_100px_40px_40px] gap-2 md:gap-4 items-center p-2 md:p-3 rounded-xl bg-gray-900 border border-transparent hover:border-gray-700 cursor-pointer transition hover:bg-gray-800"
                        >
                            {/* Icon/Index */}
                            <div className="w-8 h-8 md:w-12 md:h-12 bg-gray-800 rounded-lg flex items-center justify-center text-gray-500 group-hover:bg-red-500 group-hover:text-white transition shadow-lg shrink-0">
                                <Heart size={16} className="md:w-5 md:h-5" />
                            </div>

                            <div className="min-w-0">
                                <h4 className="font-bold text-gray-200 group-hover:text-white truncate text-sm md:text-base">{item.song.title}</h4>
                                <p className="text-xs md:text-sm text-gray-500 truncate">{item.song.artist}</p>
                            </div>

                            {/* Added At Time (Desktop Only) */}
                            <div className="hidden md:block text-right text-xs text-gray-500 font-mono">
                                {formatRelativeTime(item.createdAt)}
                            </div>

                            {/* Mobile: Remove Button */}
                            <div className="flex justify-center md:hidden">
                                <button
                                    onClick={(e) => handleRemove(item.song.id, e)}
                                    className="p-2 text-red-500 hover:bg-red-500/10 rounded-full transition"
                                >
                                    <Heart size={18} fill="currentColor" />
                                </button>
                            </div>

                            {/* Desktop: Like Button (Original Column 3) */}
                            <div className="hidden md:flex justify-center">
                                <button
                                    onClick={(e) => handleRemove(item.song.id, e)}
                                    className="p-2 text-red-500 hover:bg-red-500/10 rounded-full transition"
                                    title="Remove from Favorites"
                                >
                                    <Heart size={18} fill="currentColor" />
                                </button>
                            </div>

                            {/* Desktop: Play Button (Original Column 4) */}
                            <div className="hidden md:flex justify-center opacity-0 group-hover:opacity-100 transition">
                                <button className="p-2 bg-white text-black rounded-full shadow-lg hover:scale-110 transition">
                                    <Play size={14} fill="currentColor" className="ml-0.5" />
                                </button>
                            </div>
                        </div>
                    ))}

                    <Pagination
                        currentPage={page}
                        totalItems={total}
                        pageSize={pageSize}
                        onPageChange={setPage}
                        onPageSizeChange={handlePageSizeChange}
                    />
                </div>
            )
            }
        </div >
    );
}
