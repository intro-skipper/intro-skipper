import { Link } from '@tanstack/react-router';
import { Button } from '../ui/FormComponents';

const NotFound = () => {
  return (
    <div style={{ textAlign: 'center', padding: '50px 20px' }}>
      <h2>Page Not Found</h2>
      <p>The page you are looking for does not exist.</p>
      <Button onClick={() => {}}>
        <Link to="/">Return to Settings</Link>
      </Button>
    </div>
  );
};

export default NotFound;
