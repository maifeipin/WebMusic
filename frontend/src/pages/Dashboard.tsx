import { useEffect, useState } from 'react';
import { getStats, getUserStats } from '../services/api';
import { usePlayer, type Song } from '../context/PlayerContext';
import { Play, Clock, Heart, Music, Disc, Mic, HardDrive } from 'lucide-react';
import { Link } from 'react-router-dom';

export default function Dashboard() {
    const { playSong, playQueue } = usePlayer();
    const [stats, setStats] = useState({ totalSongs: 0, totalArtists: 0, totalAlbums: 0, totalSize: 0 });
    const [userStats, setUserStats] = useState<{
        history: { count: number, top10: Song[] },
        favorites: { count: number, top10: Song[] }
    }>({
        history: { count: 0, top10: [] },
        favorites: { count: 0, top10: [] }
    });

    useEffect(() => {
        getStats().then(res => setStats(res.data)).catch(console.error);
        getUserStats().then(res => setUserStats(res.data)).catch(console.error);
    }, []);

    const formatBytes = (bytes: number) => {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    };

    const handlePlayAll = (songs: Song[]) => {
        if (songs.length > 0) playQueue(songs);
    };

    return (
        <div className="h-full flex flex-col p-8 pb-32 space-y-6 overflow-hidden">
            <header className="flex-none flex items-center justify-between">
                <h1 className="text-3xl font-bold text-white tracking-tight">Overview</h1>
            </header>

            {/* Global Stats - Fixed Height */}
            <div className="flex-none grid grid-cols-2 lg:grid-cols-4 gap-4">
                <StatCard title="Songs" value={stats.totalSongs} icon={<Music size={20} />} color="text-blue-400" />
                <StatCard title="Artists" value={stats.totalArtists} icon={<Mic size={20} />} color="text-green-400" />
                <StatCard title="Albums" value={stats.totalAlbums} icon={<Disc size={20} />} color="text-purple-400" />
                <StatCard title="Storage" value={formatBytes(stats.totalSize)} icon={<HardDrive size={20} />} color="text-yellow-400" />
            </div>

            {/* Expandable Widgets Area */}
            <div className="flex-1 min-h-0 grid grid-cols-1 lg:grid-cols-2 gap-8">
                {/* Favorites (Now First) */}
                <Widget
                    title="Favorites"
                    count={userStats.favorites.count}
                    icon={<Heart size={20} />}
                    linkTo="/favorites"
                    onPlayAll={() => handlePlayAll(userStats.favorites.top10)}
                    accentColor="text-pink-400"
                >
                    {userStats.favorites.top10.map((song, i) => (
                        <SongRow
                            key={song.id + 'fav' + i}
                            song={song}
                            index={i}
                            onPlay={() => playSong(song)}
                        />
                    ))}
                    {userStats.favorites.top10.length === 0 && <div className="text-gray-500 text-sm p-8 text-center bg-black/20 rounded-xl my-4">No favorites yet.</div>}
                </Widget>

                {/* Recent History (Now Second) */}
                <Widget
                    title="Recently Played"
                    count={userStats.history.count}
                    icon={<Clock size={20} />}
                    linkTo="/history"
                    onPlayAll={() => handlePlayAll(userStats.history.top10)}
                    accentColor="text-blue-400"
                >
                    {userStats.history.top10.map((song, i) => (
                        <SongRow
                            key={song.id + 'hist' + i}
                            song={song}
                            index={i}
                            onPlay={() => playSong(song)}
                        />
                    ))}
                    {userStats.history.top10.length === 0 && <div className="text-gray-500 text-sm p-8 text-center bg-black/20 rounded-xl my-4">No playback history yet.</div>}
                </Widget>
            </div>
        </div>
    );
}

const StatCard = ({ title, value, icon, color }: { title: string, value: string | number, icon: React.ReactNode, color: string }) => (
    <div className="bg-gray-900 border border-gray-800 p-5 rounded-2xl flex items-center justify-between hover:border-gray-700 transition group">
        <div>
            <p className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-1">{title}</p>
            <p className={`text-2xl font-bold text-white group-hover:${color.replace('text-', 'text-opacity-80-')} transition`}>{value}</p>
        </div>
        <div className={`${color} bg-gray-800 p-3 rounded-xl opacity-80 group-hover:opacity-100 transition`}>
            {icon}
        </div>
    </div>
);

const Widget = ({ title, count, icon, children, linkTo, onPlayAll, accentColor }: any) => (
    <div className="bg-gray-900 border border-gray-800 rounded-2xl flex flex-col h-full">
        <div className="flex-none p-5 border-b border-gray-800 flex items-center justify-between">
            <div className="flex items-center gap-3">
                <div className={`p-2 rounded-lg bg-gray-800 ${accentColor}`}>
                    {icon}
                </div>
                <div>
                    <h3 className="font-bold text-white text-lg">{title}</h3>
                    <p className="text-xs text-gray-500 font-medium">{count} items</p>
                </div>
            </div>
            <div className="flex items-center gap-2">
                <button
                    onClick={onPlayAll}
                    className="p-2 bg-white text-black rounded-full hover:scale-105 transition shadow-lg active:scale-95"
                    title="Play All"
                >
                    <Play size={16} fill="currentColor" className="ml-0.5" />
                </button>
                {linkTo && (
                    <Link to={linkTo} className="text-xs font-semibold text-gray-400 hover:text-white px-3 py-2 rounded-lg hover:bg-gray-800 transition">
                        See All
                    </Link>
                )}
            </div>
        </div>
        <div className="flex-1 overflow-y-auto p-2 min-h-0">
            <div className="space-y-1">
                {children}
            </div>
        </div>
    </div>
);

const SongRow = ({ song, onPlay, index }: { song: Song, onPlay: () => void, index: number }) => (
    <div
        className="group grid grid-cols-[32px_48px_1fr_40px] gap-3 items-center p-2 rounded-xl hover:bg-gray-800/50 transition cursor-pointer border border-transparent hover:border-gray-800"
        onClick={onPlay}
    >
        {/* Index */}
        <div className="text-center text-xs font-medium text-gray-600 group-hover:text-gray-400 font-mono">
            {index + 1}
        </div>

        {/* Icon/Cover Placeholder */}
        <div className="w-10 h-10 bg-gray-800 rounded-lg flex items-center justify-center text-gray-600 group-hover:bg-gray-700 group-hover:text-white transition">
            <Music size={18} />
        </div>

        {/* Info */}
        <div className="min-w-0 flex flex-col justify-center">
            <h4 className="text-sm font-medium text-gray-200 truncate group-hover:text-white transition">{song.title}</h4>
            <p className="text-xs text-gray-500 truncate mt-0.5">{song.artist}</p>
        </div>

        {/* Action */}
        <div className="flex justify-end opacity-0 group-hover:opacity-100 transition">
            <div className="w-8 h-8 rounded-full bg-white text-black flex items-center justify-center hover:scale-110 shadow-lg">
                <Play size={14} fill="currentColor" className="ml-0.5" />
            </div>
        </div>
    </div>
);
