import { useEffect, useState } from 'react';
import { getHistory } from '../services/api';
import { usePlayer, type Song } from '../context/PlayerContext';
import { Play, Clock } from 'lucide-react';
import { formatRelativeTime } from '../utils/time';
import { Pagination } from '../components/Pagination';

export default function HistoryPage() {
    const { playSong, playQueue } = usePlayer();
    const [historyItems, setHistoryItems] = useState<{ playedAt: string, song: Song }[]>([]);
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
            const res = await getHistory(currentPage, size);
            const mapped = res.data.items.map((item: any) => ({
                playedAt: item.playedAt,
                song: {
                    ...item.mediaFile,
                    duration: item.mediaFile.duration || 0
                }
            }));
            setHistoryItems(mapped);
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

    const formatTime = (isoString: string) => {
        return formatRelativeTime(isoString);
    };

    return (
        <div className="p-4 md:p-8 pb-32 max-w-5xl mx-auto h-full box-border">
            <header className="flex items-center justify-between mb-8">
                <div className="flex items-center gap-4">
                    <div className="p-3 bg-blue-500/20 rounded-xl text-blue-400">
                        <Clock size={24} />
                    </div>
                    <div>
                        <h1 className="text-3xl font-bold text-white">History</h1>
                        <p className="text-gray-400 text-sm">Recently played songs</p>
                    </div>
                </div>
                <button
                    onClick={() => {
                        if (historyItems.length > 0) {
                            const queue = historyItems.map(h => h.song);
                            playQueue(queue, 0);
                        }
                    }}
                    disabled={historyItems.length === 0}
                    className="flex items-center gap-2 px-4 py-2 bg-blue-600 hover:bg-blue-500 disabled:bg-gray-800 disabled:text-gray-500 disabled:cursor-not-allowed text-white rounded-lg font-medium transition shadow-lg shadow-blue-900/20"
                >
                    <Play size={18} fill="currentColor" />
                    <span className="hidden md:inline">Play All</span>
                </button>
            </header>

            {loading ? (
                <div className="text-center text-gray-500 py-20">Loading...</div>
            ) : historyItems.length === 0 ? (
                <div className="text-center text-gray-500 py-20 bg-gray-900/50 rounded-2xl border border-gray-800">No history yet. Start playing!</div>
            ) : (
                <div className="space-y-1">
                    {/* Header */}
                    <div className="grid grid-cols-[40px_minmax(120px,1fr)_80px_40px] md:grid-cols-[40px_1fr_100px_40px] gap-2 md:gap-4 px-2 md:px-4 py-2 text-xs font-semibold text-gray-500 uppercase tracking-wider">
                        <div className="text-center">#</div>
                        <div>Title</div>
                        <div className="text-right">Time</div>
                        <div></div>
                    </div>

                    {historyItems.map((item, i) => (
                        <div
                            key={i}
                            onClick={() => playSong(item.song)}
                            className="group grid grid-cols-[40px_minmax(120px,1fr)_80px_40px] md:grid-cols-[40px_1fr_100px_40px] gap-2 md:gap-4 items-center p-2 md:p-3 rounded-xl bg-gray-900 border border-transparent hover:border-gray-700 cursor-pointer transition hover:bg-gray-800"
                        >
                            <div className="text-center text-sm font-mono text-gray-600 group-hover:text-blue-400">
                                {i + 1 + (page - 1) * pageSize}
                            </div>

                            <div className="min-w-0">
                                <h4 className="font-bold text-gray-200 group-hover:text-white truncate text-sm md:text-base">{item.song.title}</h4>
                                <p className="text-xs md:text-sm text-gray-500 truncate">{item.song.artist}</p>
                            </div>

                            <div className="text-right text-xs text-gray-500 font-mono">
                                {formatTime(item.playedAt)}
                            </div>

                            <div className="flex justify-center opacity-0 group-hover:opacity-100 transition">
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
            )}
        </div>
    );
}
