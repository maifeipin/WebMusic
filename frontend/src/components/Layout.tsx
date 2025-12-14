import React, { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { Home, Music, Settings, LogOut, Heart, Clock, User, ListMusic, DatabaseBackup, Tag } from 'lucide-react';
import { useAuth } from '../context/AuthContext';
import GlobalPlayer from './GlobalPlayer';
import ChangePasswordModal from './ChangePasswordModal';

import { Logo } from './Logo';
import { Menu, X } from 'lucide-react';

export const Layout = () => {
    const { logout } = useAuth();
    const navigate = useNavigate();
    const [showPasswordModal, setShowPasswordModal] = useState(false);

    // Desktop: Collapsed / Expanded
    const [isCollapsed, setIsCollapsed] = useState(false);
    // Mobile: Open / Closed (Drawer)
    const [isMobileOpen, setIsMobileOpen] = useState(false);

    const handleLogout = () => {
        logout();
        navigate('/login');
    };

    return (
        <div className="flex h-screen bg-black text-gray-200">
            {/* Mobile Header (Visible only on small screens) */}
            <div className={`md:hidden fixed top-0 left-0 right-0 h-16 bg-black/80 backdrop-blur-md border-b border-gray-800 z-40 flex items-center justify-between px-4 transition-transform duration-300 ${isMobileOpen ? '-translate-y-full' : 'translate-y-0'}`}>
                <Logo />
                <button onClick={() => setIsMobileOpen(true)} className="p-2 text-gray-400 hover:text-white">
                    <Menu size={24} />
                </button>
            </div>

            {/* Sidebar Overlay (Mobile) */}
            {isMobileOpen && (
                <div
                    className="fixed inset-0 bg-black/50 z-40 md:hidden backdrop-blur-sm"
                    onClick={() => setIsMobileOpen(false)}
                />
            )}

            {/* Sidebar */}
            <aside
                className={`
                    fixed md:static inset-y-0 left-0 z-50 bg-black border-r border-gray-900 flex flex-col transition-all duration-300
                    ${isMobileOpen ? 'translate-x-0 w-64' : '-translate-x-full md:translate-x-0'}
                    ${isCollapsed ? 'md:w-20' : 'md:w-64'}
                `}
            >
                {/* Sidebar Header */}
                <div className="h-20 flex items-center justify-between px-6">
                    <Logo
                        collapsed={isCollapsed}
                        onClick={() => {
                            if (window.innerWidth >= 768) {
                                setIsCollapsed(!isCollapsed);
                            } else {
                                setIsMobileOpen(false);
                            }
                        }}
                    />
                    {/* Mobile Close Button */}
                    <button
                        className="md:hidden text-gray-500 hover:text-white"
                        onClick={() => setIsMobileOpen(false)}
                    >
                        <X size={24} />
                    </button>
                </div>

                {/* Nav Items */}
                <nav className="flex-1 px-3 space-y-2 mt-4">
                    <NavItem to="/" icon={<Home size={20} />} label="Dashboard" collapsed={isCollapsed} />
                    <NavItem to="/library" icon={<Music size={20} />} label="Library" collapsed={isCollapsed} />
                    <NavItem to="/playlists" icon={<ListMusic size={20} />} label="My Playlists" collapsed={isCollapsed} />
                    <NavItem to="/favorites" icon={<Heart size={20} />} label="Favorites" collapsed={isCollapsed} />
                    <NavItem to="/history" icon={<Clock size={20} />} label="History" collapsed={isCollapsed} />
                    <NavItem to="/tags" icon={<Tag size={20} />} label="Tag Manager" collapsed={isCollapsed} />
                    <NavItem to="/sources" icon={<Settings size={20} />} label="Sources" collapsed={isCollapsed} />
                </nav>

                {/* Footer Actions */}
                <div className="p-3 border-t border-gray-900 space-y-2 mb-safe">
                    {/* Tools Group */}


                    <button
                        onClick={() => setShowPasswordModal(true)}
                        className={`flex items-center gap-3 text-gray-400 hover:text-white w-full p-3 rounded-lg transition hover:bg-white/5 ${isCollapsed ? 'justify-center' : ''}`}
                        title="Profile"
                    >
                        <User size={20} />
                        {!isCollapsed && <span>Profile</span>}
                    </button>

                    <NavLink
                        to="/backup"
                        className={({ isActive }) =>
                            `flex items-center gap-3 text-gray-400 hover:text-white w-full p-3 rounded-lg transition hover:bg-white/5 ${isCollapsed ? 'justify-center' : ''} ${isActive ? 'bg-white/5 text-white' : ''}`
                        }
                        title="Backup"
                    >
                        <DatabaseBackup size={20} />
                        {!isCollapsed && <span>Backup</span>}
                    </NavLink>
                    <button
                        onClick={handleLogout}
                        className={`flex items-center gap-3 text-gray-400 hover:text-white w-full p-3 rounded-lg transition hover:bg-white/5 ${isCollapsed ? 'justify-center' : ''}`}
                        title="Logout"
                    >
                        <LogOut size={20} />
                        {!isCollapsed && <span>Logout</span>}
                    </button>
                </div>
            </aside>

            {/* Main Content */}
            <main className="flex-1 overflow-y-auto relative pt-16 md:pt-0 transition-all duration-300 bg-black">
                <Outlet />
            </main>

            <GlobalPlayer />

            {showPasswordModal && <ChangePasswordModal onClose={() => setShowPasswordModal(false)} />}
        </div>
    );
};

const NavItem = ({ to, icon, label, collapsed }: { to: string, icon: React.ReactNode, label: string, collapsed?: boolean }) => (
    <NavLink
        to={to}
        className={({ isActive }) =>
            `flex items-center gap-3 px-3 py-3 rounded-lg transition font-medium ${isActive ? 'bg-blue-600/10 text-blue-500' : 'text-gray-400 hover:text-white hover:bg-white/5'} ${collapsed ? 'justify-center' : ''}`
        }
        title={collapsed ? label : undefined}
    >
        {icon}
        {!collapsed && <span className="whitespace-nowrap overflow-hidden text-ellipsis">{label}</span>}
    </NavLink>
);
