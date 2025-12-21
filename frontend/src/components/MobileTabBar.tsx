import { NavLink } from 'react-router-dom';
import { Home, Music, ListMusic, Heart } from 'lucide-react';

export default function MobileTabBar() {
    return (
        <div className="md:hidden fixed bottom-0 left-0 right-0 bg-black/95 backdrop-blur-lg border-t border-white/10 flex items-center justify-around z-40 pb-[env(safe-area-inset-bottom)]">
            <Tab to="/" icon={<Home size={24} />} label="Home" />
            <Tab to="/library" icon={<Music size={24} />} label="Library" />
            <Tab to="/playlists" icon={<ListMusic size={24} />} label="Playlists" />
            <Tab to="/favorites" icon={<Heart size={24} />} label="Liked" />
        </div>
    );
}

const Tab = ({ to, icon, label }: { to: string, icon: React.ReactNode, label: string }) => (
    <NavLink
        to={to}
        className={({ isActive }) =>
            `flex flex-col items-center justify-center w-full py-3 gap-1 transition-colors ${isActive ? 'text-white' : 'text-gray-500 active:text-gray-300'}`
        }
    >
        {icon}
        <span className="text-[10px] font-medium">{label}</span>
    </NavLink>
);
